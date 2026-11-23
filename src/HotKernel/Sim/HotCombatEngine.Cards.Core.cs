using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Evaluates any of the 52 STS2 0.113.1 calculated card variables matching
    /// <c>CalculatedVarSpecRegistry.TryCalculate</c> with zero heap allocations.
    /// </summary>
    internal static int EvaluateCalcVar(
        in CombatHotState s,
        HotRules rules,
        HotExecScratch scratch,
        in HotCard card,
        HotCardDef def,
        int targetEnemySlot,
        int currentPlayEnergySpent = 0)
    {
        HotCalcVarKind kind = def.CalcVar != HotCalcVarKind.None
            ? def.CalcVar
            : (HotCalcVarKind)def.CalcVarKind;

        decimal calcBase = GetNamedVarDecimal(in card, def, "CalculationBase", def.GetDamage(card.UpgradeLevel, card.Var0Delta));
        decimal calcExtra = HasNamedVar(def, "CalculationExtra")
            ? GetNamedVarDecimal(in card, def, "CalculationExtra", 1m)
            : GetNamedVarDecimal(in card, def, "ExtraDamage", 1m);

        decimal mult = EvaluateCalcVarMultiplier(in s, rules, in card, def, kind, targetEnemySlot, currentPlayEnergySpent);
        decimal result = calcBase + calcExtra * mult;
        return Math.Max(0, (int)Math.Floor(result));
    }

    internal static decimal EvaluateCalcVarMultiplier(
        in CombatHotState s,
        HotRules rules,
        in HotCard card,
        HotCardDef def,
        HotCalcVarKind kind,
        int targetEnemySlot,
        int currentPlayEnergySpent = 0)
    {
        byte targetCreatureSlot = targetEnemySlot >= 0 && targetEnemySlot < s.EnemyCount
            ? (byte)(3 + targetEnemySlot)
            : (byte)0xFF;

        switch (kind)
        {
            case HotCalcVarKind.PreciseCut:
                return -s.HandCount;

            case HotCalcVarKind.MelodicShot:
            case HotCalcVarKind.HarmonicShield:
            case HotCalcVarKind.SteadyCadence:
            case HotCalcVarKind.RhythmStrike:
                return CountMelodiesPlayedThisTurn(in s);

            case HotCalcVarKind.WahSymphony:
            {
                int count = 0;
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].MelodyTagMask != 0)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.TheShot:
                return card.CardPrivate;

            case HotCalcVarKind.Anguish:
                return s.Counters[(int)HotCounterSlot.SadnessGeneratedBefore];

            case HotCalcVarKind.FiendFire:
            {
                int count = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (s.Cards[i].CardUid != card.CardUid)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.Growth:
            {
                int count = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].CardId == HotCustomCardId.Sadness)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.MyShinies:
            {
                decimal goldDiv = GetNamedVarDecimal(in card, def, "Gold", card.UpgradeLevel > 0 ? 10m : 20m);
                return goldDiv > 0m ? s.Gold / goldDiv : 0m;
            }

            case HotCalcVarKind.TheTower:
                return rules.TotalFloor;

            case HotCalcVarKind.Stack:
                return s.DiscardCount;

            case HotCalcVarKind.Squeeze:
            {
                int count = 0;
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    if (s.Cards[i].CardUid != card.CardUid && rules.Cards[s.Cards[i].CardDefId].IsOstyAttackTag)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.Mirage:
            {
                ushort poisonId = rules.GetPowerDefId(HotPowerId.PoisonPower);
                if (poisonId == 0)
                    return 0m;
                int sum = 0;
                for (int r = 0; r < s.RosterCount; r++)
                {
                    byte eSlot = s.ActiveEnemyRoster[r];
                    if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                        sum += GetPowerAmount(in s, (byte)(3 + eSlot), poisonId);
                }
                return sum;
            }

            case HotCalcVarKind.Rattle:
                return 1 + s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn];

            case HotCalcVarKind.MindBlast:
                return s.DrawCount;

            case HotCalcVarKind.GangUp:
                return 0m; // Single-player combat has no other player teammates

            case HotCalcVarKind.Mimic:
                return targetEnemySlot >= 0 && targetEnemySlot < s.EnemyCount
                    ? s.Enemies[targetEnemySlot].Vital.Block
                    : 0;

            case HotCalcVarKind.KnifeTrap:
            {
                int count = 0;
                int start = s.ExhaustStart;
                int end = start + s.ExhaustCount;
                for (int i = start; i < end; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].IsShivTag)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.Unleash:
                return (s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0
                    ? s.Osty.CurrentHp
                    : 0;

            case HotCalcVarKind.Radiate:
                return s.Counters[(int)HotCounterSlot.StarsGainedThisTurn];

            case HotCalcVarKind.PerfectedStrike:
            {
                int count = 0;
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].IsStrikeTag)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.SovereignBlade:
                return GetPowerAmountById(in s, rules, ownerSlot: 0, HotPowerId.ParryPower);

            case HotCalcVarKind.Supermassive:
                return s.HistoryCardsGenerated;

            case HotCalcVarKind.Sacrifice:
                return (s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0
                    ? s.Osty.MaxHp * 3
                    : 0;

            case HotCalcVarKind.TimesUp:
                return targetCreatureSlot != 0xFF
                    ? GetPowerAmountById(in s, rules, targetCreatureSlot, HotPowerId.DoomPower)
                    : 0;

            case HotCalcVarKind.SoulStorm:
            {
                int count = 0;
                int start = s.ExhaustStart;
                int end = start + s.ExhaustCount;
                for (int i = start; i < end; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].CardId == HotCustomCardId.Soul)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.Voltaic:
                return s.HistoryLightningChannels;

            case HotCalcVarKind.TearAsunder:
                return 1 + s.HistoryUnblockedHitsReceived;

            case HotCalcVarKind.HelixDrill:
                return Math.Max(0, s.Counters[(int)HotCounterSlot.EnergySpentThisTurn] - currentPlayEnergySpent);

            case HotCalcVarKind.PullFromBelow:
                return s.HistoryEtherealPlays;

            case HotCalcVarKind.Normality:
                return Math.Min(3, s.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn]);

            case HotCalcVarKind.Synchronize:
            case HotCalcVarKind.CompileDriver:
                return CountDistinctOrbTypes(in s);

            case HotCalcVarKind.Protector:
                return (s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0
                    ? s.Osty.MaxHp
                    : 0;

            case HotCalcVarKind.NoEscape:
            {
                if (targetCreatureSlot == 0xFF)
                    return 0m;
                decimal threshold = GetNamedVarDecimal(in card, def, "DoomThreshold", 10m);
                if (threshold <= 0m)
                    return 0m;
                int doom = GetPowerAmountById(in s, rules, targetCreatureSlot, HotPowerId.DoomPower);
                return Math.Floor((decimal)doom / threshold);
            }

            case HotCalcVarKind.Flechettes:
            {
                int count = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Skill)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.Rend:
            {
                if (targetCreatureSlot == 0xFF)
                    return 0m;
                int count = 0;
                for (int i = 0; i < s.PowerCount; i++)
                {
                    ref readonly HotPower p = ref s.Powers[i];
                    if (p.OwnerSlot != targetCreatureSlot || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                        continue;
                    HotPowerDef pDef = rules.Powers[p.PowerDefId];
                    bool isDebuff = (pDef.Type == PowerType.Debuff && p.Amount > 0)
                        || (pDef.Type == PowerType.Buff && p.Amount < 0);
                    if (isDebuff && !TryGetTemporaryStatInfo(pDef.PowerId, out _, out _))
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.GoldAxe:
                return s.HistoryFinishedPlays;

            case HotCalcVarKind.FlakCannon:
            {
                int count = 0;
                int nonExhaustEnd = s.ExhaustStart;
                for (int i = 0; i < nonExhaustEnd; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Status)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.LunarBlast:
                return s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn];

            case HotCalcVarKind.Murder:
                return s.HistoryCardsDrawn;

            case HotCalcVarKind.Finisher:
                return s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn];

            case HotCalcVarKind.Bully:
                return targetCreatureSlot != 0xFF
                    ? GetPowerAmountById(in s, rules, targetCreatureSlot, HotPowerId.VulnerablePower)
                    : 0;

            case HotCalcVarKind.BeatIntoShape:
                return targetEnemySlot >= 0 && targetEnemySlot < s.EnemyCount
                    ? s.Enemies[targetEnemySlot].PoweredHitsFromPlayer
                    : 0;

            case HotCalcVarKind.DeathMarch:
                return s.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn];

            case HotCalcVarKind.DemonicShield:
            case HotCalcVarKind.BodySlam:
                return s.Player.Block;

            case HotCalcVarKind.Barrage:
                return s.OrbCount;

            case HotCalcVarKind.CrescentSpear:
            {
                int count = 0;
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    ref readonly HotCard c = ref s.Cards[i];
                    if (c.BaseStarCost >= 0 || (c.Flags & HotCardFlags.HasStarCostX) != 0)
                        count++;
                }
                return count;
            }

            case HotCalcVarKind.AshenStrike:
                return s.ExhaustCount;

            default:
                return 0m;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountMelodiesPlayedThisTurn(in CombatHotState s)
    {
        int count = 0;
        for (int m = 0; m < 3; m++)
        {
            if (s.Fool.MelodyStarted[m] > 0)
                count++;
        }
        return count;
    }

    internal static void ExecuteEnchantmentOnPlay(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int playOffsetInPlay,
        int targetEnemySlot)
    {
        int livePlayIdx = s.PlayStart + playOffsetInPlay;
        if (livePlayIdx >= s.TotalCardCount)
            return;
        byte enchId = s.Cards[livePlayIdx].EnchId;
        if (enchId == 0 || enchId >= rules.Enchantments.Length)
            return;

        HotEnchantmentId kind = rules.Enchantments[enchId].EnchantmentId;
        short amt = s.Cards[livePlayIdx].EnchAmt;
        byte status = s.Cards[livePlayIdx].EnchStatus;

        switch (kind)
        {
            case HotEnchantmentId.Adroit:
                GainBlockInternal(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amt, isCardBlock: true, cardIdx: livePlayIdx, cardDefId: s.Cards[livePlayIdx].CardDefId);
                break;

            case HotEnchantmentId.Corrupted:
                DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: 0, baseDamage: 2, isPoweredAttack: false, isUnblockable: true);
                break;

            case HotEnchantmentId.Inky:
            {
                // STS2 0.113.1 Inky.cs:17,19,35: ShowAmount => false, CanonicalVars => PowerVar<WeakPower>(1m) (always 1).
                const int weakAmt = 1;
                HotCardDef def = rules.Cards[s.Cards[livePlayIdx].CardDefId];
                if (GetEffectiveCardTargetType(in s, in s.Cards[livePlayIdx], def, rules) == TargetType.AllEnemies)
                    ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, weakAmt, isCardSource: true);
                else if (IsValidEnemyTarget(in s, targetEnemySlot))
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.WeakPower, weakAmt, isCardSource: true);
                break;
            }

            case HotEnchantmentId.Momentum:
                s.Cards[livePlayIdx].EnchPrivate = (short)(s.Cards[livePlayIdx].EnchPrivate + amt);
                break;

            case HotEnchantmentId.Sown:
                if (status == 0 && amt > 0)
                    GainEnergy(ref s, rules, amt);
                break;

            case HotEnchantmentId.Swift:
                if (status == 0 && amt > 0)
                    DrawCards(ref s, ref cold, rules, scratch, amt, fromHandDraw: false);
                break;
        }
    }

    internal static void ExecuteEnchantmentAfterPlay(
        ref CombatHotState s,
        HotRules rules,
        int cardIdx)
    {
        if (cardIdx < 0 || cardIdx >= s.TotalCardCount)
            return;
        byte enchId = s.Cards[cardIdx].EnchId;
        if (enchId == 0 || enchId >= rules.Enchantments.Length)
            return;

        HotEnchantmentId kind = rules.Enchantments[enchId].EnchantmentId;
        switch (kind)
        {
            case HotEnchantmentId.Sown:
            case HotEnchantmentId.Swift:
            case HotEnchantmentId.Glam:
            case HotEnchantmentId.Vigorous:
                s.Cards[cardIdx].EnchStatus = 1; // Disabled
                break;

            case HotEnchantmentId.Goopy:
                s.Cards[cardIdx].EnchAmt++;
                break;
        }
    }

    /// <summary>
    /// Executes bespoke custom cards for Ironclad, Silent, Defect, Colorless, Status, Curse,
    /// Event, and Token domains. Regent/Necrobinder and Fool/Tarot custom cards are dispatched to
    /// <see cref="HotRegentNecro"/> and <see cref="HotFoolTarot"/>.
    /// </summary>
    internal static void ExecuteCoreCustomCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCustomCardId cardId,
        int playOffsetInPlay,
        int targetEnemySlot,
        int energySpent)
    {
        int livePlayIdx = s.PlayStart + playOffsetInPlay;
        HotCard playedCard = s.Cards[livePlayIdx];
        HotCardDef def = rules.Cards[playedCard.CardDefId];
        byte upgrade = playedCard.UpgradeLevel;
        int baseDmg = def.GetDamage(upgrade, playedCard.Var0Delta);
        int baseBlk = def.GetBlock(upgrade, playedCard.Var1Delta);
        int magic = def.GetMagic(upgrade);
        int hits = def.GetHits(upgrade);

        // KnowledgeDemon.IChoosable curse tokens only apply their power in OnChosen(), NOT OnPlay() (0.113.1).
        if (cardId is HotCustomCardId.Amnesia
            or HotCustomCardId.Disintegration
            or HotCustomCardId.MindRot
            or HotCustomCardId.Sloth
            or HotCustomCardId.WasteAway)
        {
            return;
        }

        // Route Regent/Necrobinder, Fool/Tarot, and cross-domain Token custom cards
        if (def.Domain is HotCardDomain.Regent or HotCardDomain.Necrobinder)
        {
            HotRegentNecro.TryExecuteCustomCard(ref s, ref cold, rules, scratch, playedCard.CardDefId, ref s.Cards[livePlayIdx], targetEnemySlot, out _);
            return;
        }
        if (def.Domain is HotCardDomain.Fool or HotCardDomain.Tarot)
        {
            HotFoolTarot.TryExecuteCustomCard(ref s, ref cold, rules, scratch, playedCard.CardDefId, ref s.Cards[livePlayIdx], targetEnemySlot, out _);
            return;
        }
        if (def.Domain == HotCardDomain.Token)
        {
            if (HotRegentNecro.TryExecuteCustomCard(ref s, ref cold, rules, scratch, playedCard.CardDefId, ref s.Cards[livePlayIdx], targetEnemySlot, out _))
                return;
            if (HotFoolTarot.TryExecuteCustomCard(ref s, ref cold, rules, scratch, playedCard.CardDefId, ref s.Cards[livePlayIdx], targetEnemySlot, out _))
                return;
        }

        switch (cardId)
        {
            // ===================== IRONCLAD =====================
            case HotCustomCardId.Armaments:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (upgrade > 0)
                {
                    for (int i = 0; i < s.HandCount; i++)
                        UpgradeCard(ref s, rules, i);
                }
                else if (HasAnyUpgradableCardInHand(in s, rules))
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        UpgradeCard(ref s, rules, idx);
                }
                break;

            case HotCustomCardId.AshenStrike:
            case HotCustomCardId.BodySlam:
            case HotCustomCardId.Bully:
            case HotCustomCardId.PerfectedStrike:
            case HotCustomCardId.PreciseCut:
            case HotCustomCardId.Murder:
            case HotCustomCardId.MindBlast:
            case HotCustomCardId.GoldAxe:
            case HotCustomCardId.Rend:
            {
                int calcDmg = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, targetEnemySlot, energySpent);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, calcDmg, 1, powered: true);
                break;
            }

            case HotCustomCardId.Bloodletting:
            {
                int hpLoss = GetNamedVarInt(in playedCard, def, "HpLoss", 3);
                int eGain = GetNamedVarInt(in playedCard, def, "Energy", magic > 0 ? magic : 2);
                DealDamage(ref s, ref cold, rules, scratch, 0, 0, hpLoss, isPoweredAttack: false, isUnblockable: true);
                if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                    GainEnergy(ref s, rules, eGain);
                break;
            }

            case HotCustomCardId.Brand:
            {
                int hpLoss = GetNamedVarInt(in playedCard, def, "HpLoss", 1);
                DealDamage(ref s, ref cold, rules, scratch, 0, 0, hpLoss, isPoweredAttack: false, isUnblockable: true);
                if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0 && s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                }
                if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                {
                    int str = GetNamedVarInt(in playedCard, def, "StrengthPower", magic > 0 ? magic : 1);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, str, isCardSource: true);
                }
                break;
            }

            case HotCustomCardId.BurningPact:
                if (s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                }
                if (!scratch.NeedsChoice)
                    DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 2, fromHandDraw: false);
                break;

            case HotCustomCardId.Cascade:
            {
                int x = Math.Max(0, (int)playedCard.CapturedEnergy) + (upgrade > 0 ? 1 : 0);
                AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, x, forceExhaust: false);
                break;
            }

            case HotCustomCardId.Cinder:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], s.HandCount);
                    ExhaustCard(ref s, ref cold, rules, scratch, pick, causedByEthereal: false);
                }
                break;

            case HotCustomCardId.Conflagration:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, hits, powered: true);
                break;

            case HotCustomCardId.DemonicShield:
                GainBlock(ref s, ref cold, rules, scratch, 0, s.Player.Block, isCardBlock: false);
                break;

            case HotCustomCardId.Dismantle:
            {
                int dismantleHits = IsValidEnemyTarget(in s, targetEnemySlot)
                    && GetPowerAmountById(in s, rules, (byte)(3 + targetEnemySlot), HotPowerId.VulnerablePower) > 0
                    ? Math.Max(2, hits)
                    : 1;
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, dismantleHits, powered: true);
                break;
            }

            case HotCustomCardId.Dominate:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte enemyCreatureSlot = (byte)(3 + targetEnemySlot);
                    int vulnAmt = GetNamedVarInt(in playedCard, def, "VulnerablePower", magic > 0 ? magic : 1);
                    ApplyPowerById(ref s, ref cold, rules, scratch, enemyCreatureSlot, 0, HotPowerId.VulnerablePower, vulnAmt, isCardSource: true);
                    if (!scratch.NeedsChoice)
                    {
                        int curVuln = GetPowerAmountById(in s, rules, enemyCreatureSlot, HotPowerId.VulnerablePower);
                        if (curVuln > 0)
                            ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, curVuln, isCardSource: true);
                    }
                }
                break;

            case HotCustomCardId.DrumOfBattle:
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 2), fromHandDraw: false);
                break;

            case HotCustomCardId.EvilEye:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn] > 0)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                break;

            case HotCustomCardId.ExpectAFight:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                {
                    int eDelta = GetNamedVarInt(in playedCard, def, "Energy", magic > 0 ? magic : 1);
                    for (int i = 0; i < s.HandCount; i++)
                    {
                        ref HotCard hc = ref s.Cards[i];
                        if (rules.Cards[hc.CardDefId].Type == CardType.Attack && (hc.Flags & HotCardFlags.CostsX) == 0)
                            CostAddUntilPlayed(ref hc, ref cold, -eDelta);
                    }
                }
                break;

            case HotCustomCardId.Feed:
            {
                bool wasAlive = IsValidEnemyTarget(in s, targetEnemySlot);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (wasAlive && !scratch.NeedsChoice && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                {
                    int maxHpGain = GetNamedVarInt(in playedCard, def, "MaxHp", magic > 0 ? magic : 3);
                    GainMaxHp(ref s, ref cold, rules, scratch, 0, maxHpGain);
                }
                break;
            }

            case HotCustomCardId.FiendFire:
            {
                int handBefore = s.HandCount;
                for (int i = 0; i < handBefore && s.HandCount > 0; i++)
                {
                    ExhaustCard(ref s, ref cold, rules, scratch, 0, causedByEthereal: false);
                    if (scratch.NeedsChoice)
                        return;
                }
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, s.PlayStart + playOffsetInPlay, targetEnemySlot, baseDmg, handBefore, powered: true);
                break;
            }

            case HotCustomCardId.FightMe:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int playerStr = GetNamedVarInt(in playedCard, def, "StrengthPower", magic > 0 ? magic : 2);
                    int enemyStr = GetNamedVarInt(in playedCard, def, "EnemyStrength", 1);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, playerStr, isCardSource: true);
                    if (IsValidEnemyTarget(in s, targetEnemySlot))
                        ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.StrengthPower, enemyStr, isCardSource: true);
                }
                break;

            case HotCustomCardId.FlameBarrier:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                {
                    int dmgBack = GetNamedVarInt(in playedCard, def, "DamageBack", magic > 0 ? magic : 4);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FlameBarrierPower, dmgBack, isCardSource: true);
                }
                break;

            case HotCustomCardId.ForgottenRitual:
                if (s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn] > 0)
                    GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", magic > 0 ? magic : 3));
                break;

            case HotCustomCardId.Havoc:
                AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, 1, forceExhaust: true);
                break;

            case HotCustomCardId.Headbutt:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.DiscardCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                    if (idx >= 0)
                        HotPiles.MoveCard(ref s, idx, HotPileKind.Draw, 0);
                }
                break;

            case HotCustomCardId.InfernalBlade:
                if (rules.CombatCharacterAttackPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[1];
                    int got = RollDistinctFromPool(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules, rules.CombatCharacterAttackPool, picked);
                    if (got > 0)
                    {
                        int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[0], HotPileKind.Hand, -1, false, true);
                        if (idx >= 0)
                            SetCardToFreeThisTurn(ref s.Cards[idx], ref cold);
                    }
                }
                break;

            case HotCustomCardId.MoltenFist:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte eSlot = (byte)(3 + targetEnemySlot);
                    int curVuln = GetPowerAmountById(in s, rules, eSlot, HotPowerId.VulnerablePower);
                    if (curVuln > 0)
                        ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.VulnerablePower, curVuln, isCardSource: true);
                }
                break;

            case HotCustomCardId.Offering:
            {
                int hpLoss = GetNamedVarInt(in playedCard, def, "HpLoss", 6);
                int eGain = GetNamedVarInt(in playedCard, def, "Energy", 2);
                int draw = GetNamedVarInt(in playedCard, def, "Cards", magic > 0 ? magic : 3);
                DealDamage(ref s, ref cold, rules, scratch, 0, 0, hpLoss, isPoweredAttack: false, isUnblockable: true);
                if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                {
                    GainEnergy(ref s, rules, eGain);
                    DrawCards(ref s, ref cold, rules, scratch, draw, fromHandDraw: false);
                }
                break;
            }

            case HotCustomCardId.PactsEnd:
                if (s.ExhaustCount >= GetNamedVarInt(in playedCard, def, "Cards", magic > 0 ? magic : 3))
                    ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                break;

            case HotCustomCardId.Pillage:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    // STS2 0.113.1 Pillage.cs:28-33: inspect the actually drawn CardModel's Type,
                    // not Hand[^1] (AfterCardDrawn hooks like HellraiserPower may auto-play the drawn card,
                    // leaving Hand empty or ending with a different card).
                    int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                    while (!IsOverOrEnding(in s) && s.HandCount < maxHand)
                    {
                        int drawn = DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false, out ushort drawnCardDefId);
                        if (drawn <= 0 || scratch.NeedsChoice || drawnCardDefId == 0 || drawnCardDefId >= rules.Cards.Length)
                            break;
                        if (rules.Cards[drawnCardDefId].Type != CardType.Attack)
                            break;
                    }
                }
                break;

            case HotCustomCardId.PrimalForce:
            {
                ushort rockId = rules.GetCardDefId(HotCustomCardId.GiantRock);
                if (rockId != 0)
                {
                    for (int i = 0; i < s.HandCount && !IsOverOrEnding(in s); i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Attack)
                        {
                            TransformCardInPlace(ref s, ref cold, rules, scratch, i, rockId, upgrade > 0);
                            if (scratch.NeedsChoice)
                                return;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Rampage:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                {
                    int curIdx = s.PlayStart + playOffsetInPlay;
                    int inc = GetNamedVarInt(in playedCard, def, "Increase", magic > 0 ? magic : 5);
                    s.Cards[curIdx].Var0Delta = (short)(s.Cards[curIdx].Var0Delta + inc);
                    s.Cards[curIdx].CardPrivate += inc;
                }
                break;

            case HotCustomCardId.SecondWind:
            {
                // STS2 0.113.1 SecondWind.cs:29-33: snapshot non-Attack hand cards via GetCards().ToList() BEFORE exhausting,
                // so cards drawn mid-loop (e.g. DarkEmbrace) are NOT exhausted.
                Span<ushort> nonAtkUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
                int nonAtkCount = 0;
                for (int i = 0; i < s.HandCount && nonAtkCount < nonAtkUids.Length; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type != CardType.Attack)
                        nonAtkUids[nonAtkCount++] = s.Cards[i].CardUid;
                }
                for (int i = 0; i < nonAtkCount && !IsOverOrEnding(in s); i++)
                {
                    int idx = FindCardIndexByUidAnyPile(in s, nonAtkUids[i]);
                    if (idx >= 0 && HotPiles.PileOfIndex(in s, idx) != HotPileKind.Exhaust)
                    {
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                        if (scratch.NeedsChoice)
                            return;
                    }
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                    if (scratch.NeedsChoice)
                        return;
                }
                break;
            }

            case HotCustomCardId.Spite:
            {
                int spiteHits = s.Counters[(int)HotCounterSlot.HasLostHpThisTurn] > 0 ? Math.Max(2, hits) : 1;
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, spiteHits, powered: true);
                break;
            }

            case HotCustomCardId.Stoke:
            {
                int handCount = s.HandCount;
                Span<ushort> handUids = handCount <= HotLimits.MaxStackAllocElements
                    ? stackalloc ushort[HotLimits.MaxStackAllocElements]
                    : new ushort[handCount];
                for (int i = 0; i < handCount; i++)
                    handUids[i] = s.Cards[i].CardUid;

                for (int i = 0; i < handCount; i++)
                {
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, handUids[i]);
                    if (idx >= 0)
                    {
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }

                if (handCount > 0 && !IsOverOrEnding(in s))
                {
                    GenerateRandomFromPoolWithReplacement(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        rules.UnlockedCharacterCardPool,
                        handCount,
                        upgraded: upgrade > 0,
                        zeroCostOnly: false);
                }
                break;
            }

            case HotCustomCardId.Stomp:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                break;

            case HotCustomCardId.TearAsunder:
            {
                int tearHits = Math.Max(1, EvaluateCalcVar(in s, rules, scratch, in playedCard, def, targetEnemySlot, energySpent));
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, tearHits, powered: true);
                break;
            }

            case HotCustomCardId.Thrash:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    Span<int> atkIndices = stackalloc int[HotLimits.MaxStackAllocElements];
                    int atkCount = 0;
                    for (int i = 0; i < s.HandCount && atkCount < atkIndices.Length; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Attack)
                            atkIndices[atkCount++] = i;
                    }
                    if (atkCount > 0)
                    {
                        int chosenIdx = atkIndices[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], atkCount)];
                        HotCard exhCard = s.Cards[chosenIdx];
                        HotCardDef exhDef = rules.Cards[exhCard.CardDefId];
                        int rawDmg = exhDef.CalcVar != HotCalcVarKind.None
                            ? EvaluateCalcVar(in s, rules, scratch, in exhCard, exhDef, -1, 0)
                            : (HasNamedVar(exhDef, "Damage")
                                ? exhDef.GetDamage(exhCard.UpgradeLevel, exhCard.Var0Delta)
                                : GetNamedVarInt(in exhCard, exhDef, "OstyDamage", exhDef.GetDamage(exhCard.UpgradeLevel, exhCard.Var0Delta)));
                        decimal modifiedDmg = ComputeModifiedDamage(
                            ref s,
                            ref cold,
                            rules,
                            scratch,
                            dealerSlot: 0,
                            targetSlot: 0xFF,
                            baseDamage: rawDmg,
                            isPoweredAttack: true,
                            cardIdx: chosenIdx,
                            cardDefId: exhCard.CardDefId);
                        int addDmg = (int)Math.Floor(modifiedDmg);
                        int curPlay = s.PlayStart + playOffsetInPlay;
                        s.Cards[curPlay].Var0Delta = (short)(s.Cards[curPlay].Var0Delta + addDmg);
                        ExhaustCard(ref s, ref cold, rules, scratch, chosenIdx, causedByEthereal: false);
                    }
                }
                break;
            }

            case HotCustomCardId.TrueGrit:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    if (upgrade > 0)
                    {
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                        if (idx >= 0)
                            ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                    }
                    else
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], s.HandCount);
                        ExhaustCard(ref s, ref cold, rules, scratch, pick, causedByEthereal: false);
                    }
                }
                break;

            case HotCustomCardId.Uppercut:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int pAmt = GetNamedVarInt(in playedCard, def, "Power", magic > 0 ? magic : 1);
                    byte eSlot = (byte)(3 + targetEnemySlot);
                    ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.WeakPower, pAmt, isCardSource: true);
                    ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.VulnerablePower, pAmt, isCardSource: true);
                }
                break;

            case HotCustomCardId.Whirlwind:
            {
                int x = Math.Max(0, (int)playedCard.CapturedEnergy);
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, x, powered: true);
                break;
            }

            // ===================== SILENT =====================
            case HotCustomCardId.Abrasive:
            {
                int dex = GetNamedVarInt(in playedCard, def, "DexterityPower", 1);
                int thorns = GetNamedVarInt(in playedCard, def, "ThornsPower", magic > 0 ? magic : 4);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, dex, isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ThornsPower, thorns, isCardSource: true);
                break;
            }

            case HotCustomCardId.Acrobatics:
                DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 3, fromHandDraw: false);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        DiscardCard(ref s, ref cold, rules, scratch, idx);
                }
                break;

            case HotCustomCardId.Adrenaline:
                GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", upgrade > 0 ? 2 : 1));
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 2), fromHandDraw: false);
                break;

            case HotCustomCardId.BladeDance:
                GenerateShivsToHand(ref s, ref cold, rules, scratch, magic > 0 ? magic : 3, upgraded: false, inky: false);
                break;

            case HotCustomCardId.BladeOfInk:
                GenerateShivsToHand(ref s, ref cold, rules, scratch, magic > 0 ? magic : 2, upgraded: false, inky: true);
                break;

            case HotCustomCardId.BouncingFlask:
            {
                int repeat = GetNamedVarInt(in playedCard, def, "Repeat", 3);
                int poison = GetNamedVarInt(in playedCard, def, "PoisonPower", magic > 0 ? magic : 3);
                for (int r = 0; r < repeat; r++)
                {
                    int eSlot = PickRandomLivingEnemySlot(ref s);
                    if (eSlot < 0)
                        break;
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + eSlot), 0, HotPowerId.PoisonPower, poison, isCardSource: true);
                }
                break;
            }

            case HotCustomCardId.BubbleBubble:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte eSlot = (byte)(3 + targetEnemySlot);
                    if (GetPowerAmountById(in s, rules, eSlot, HotPowerId.PoisonPower) > 0)
                        ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.PoisonPower, GetNamedVarInt(in playedCard, def, "PoisonPower", magic > 0 ? magic : 9), isCardSource: true);
                }
                break;

            case HotCustomCardId.BulletTime:
                for (int i = 0; i < s.HandCount; i++)
                {
                    if ((s.Cards[i].Flags & HotCardFlags.CostsX) == 0)
                        SetCardToFreeThisTurn(ref s.Cards[i], ref cold);
                }
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.NoDrawPower, 1, isCardSource: true);
                break;

            case HotCustomCardId.CalculatedGamble:
            {
                int handBefore = s.HandCount;
                if (handBefore > 0)
                {
                    int cap = Math.Min(handBefore, HotLimits.MaxStackAllocElements);
                    Span<ushort> handUids = stackalloc ushort[cap];
                    for (int i = 0; i < cap; i++)
                        handUids[i] = s.Cards[i].CardUid;
                    DiscardCardsAndDraw(ref s, ref cold, rules, scratch, handUids, drawCount: cap);
                }
                break;
            }

            case HotCustomCardId.CloakAndDagger:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                    GenerateShivsToHand(ref s, ref cold, rules, scratch, magic > 0 ? magic : 1, upgraded: false, inky: false);
                break;

            case HotCustomCardId.DaggerSpray:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, Math.Max(2, hits), powered: true);
                break;

            case HotCustomCardId.DaggerThrow:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                    if (!scratch.NeedsChoice && s.HandCount > 0)
                    {
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                        if (idx >= 0)
                            DiscardCard(ref s, ref cold, rules, scratch, idx);
                    }
                }
                break;

            case HotCustomCardId.DeadlyPoison:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.PoisonPower, GetNamedVarInt(in playedCard, def, "PoisonPower", magic > 0 ? magic : 5), isCardSource: true);
                break;

            case HotCustomCardId.DodgeAndRoll:
            {
                int gained = GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && gained > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, gained, isCardSource: true);
                break;
            }

            case HotCustomCardId.EchoingSlash:
            {
                int waves = 1;
                while (waves > 0 && !IsOverOrEnding(in s))
                {
                    waves--;
                    int aliveBefore = CountLivingEnemies(in s);
                    int curPlayIdx = s.PlayStart + playOffsetInPlay;
                    int waveDmg = (uint)curPlayIdx < (uint)s.TotalCardCount
                        ? def.GetDamage(s.Cards[curPlayIdx].UpgradeLevel, s.Cards[curPlayIdx].Var0Delta)
                        : baseDmg;
                    ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, curPlayIdx, waveDmg, 1, powered: true);
                    if (scratch.NeedsChoice)
                        return;
                    int killed = Math.Max(0, aliveBefore - CountLivingEnemies(in s));
                    waves += killed;
                }
                break;
            }

            case HotCustomCardId.EscapePlan:
            {
                // STS2 0.113.1 EscapePlan.cs:25-29: inspect the actually drawn CardModel's Type, not Hand[^1].
                int drawn = DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false, out ushort drawnCardDefId);
                if (!scratch.NeedsChoice && drawn > 0 && drawnCardDefId > 0 && drawnCardDefId < rules.Cards.Length
                    && rules.Cards[drawnCardDefId].Type == CardType.Skill)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                break;
            }

            case HotCustomCardId.Expose:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    byte eSlot = (byte)(3 + targetEnemySlot);
                    s.Enemies[targetEnemySlot].Vital.Block = 0;
                    ushort artId = rules.ArtifactPowerDefId != 0 ? rules.ArtifactPowerDefId : rules.GetPowerDefId(HotPowerId.ArtifactPower);
                    if (artId != 0 && GetPowerAmount(in s, eSlot, artId) > 0)
                        SetPowerAmount(ref s, rules, eSlot, artId, 0);
                    ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.VulnerablePower, GetNamedVarInt(in playedCard, def, "Power", magic > 0 ? magic : 2), isCardSource: true);
                }
                break;

            case HotCustomCardId.FanOfKnives:
                GenerateShivsToHand(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Shivs", magic > 0 ? magic : 4), upgraded: false, inky: false);
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FanOfKnivesPower, 1, isCardSource: true);
                break;

            case HotCustomCardId.Finisher:
            {
                int atkHits = s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn];
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, atkHits, powered: true);
                break;
            }

            case HotCustomCardId.Flechettes:
            {
                int skillCount = 0;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Skill)
                        skillCount++;
                }
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, skillCount, powered: true);
                break;
            }

            case HotCustomCardId.GrandFinale:
                if (s.DrawCount == 0)
                    ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                break;

            case HotCustomCardId.HandTrick:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && HasEligibleHandCardForFilter(in s, rules, HotCustomCardId.HandTrick))
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        s.Cards[idx].Flags |= HotCardFlags.IsSlyThisTurn;
                }
                break;

            case HotCustomCardId.Haze:
            {
                int poison = GetNamedVarInt(in playedCard, def, "PoisonPower", upgrade > 0 ? 6 : 4);
                int weak = GetNamedVarInt(in playedCard, def, "WeakPower", upgrade > 0 ? 2 : 1);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PoisonPower, poison, isCardSource: true);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, weak, isCardSource: true);
                break;
            }

            case HotCustomCardId.HiddenDaggers:
            {
                int discCount = Math.Min(GetNamedVarInt(in playedCard, def, "Cards", 2), Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction));
                Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                int pickedCount = 0;
                scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                for (int k = 0; k < discCount && pickedCount < s.HandCount; k++)
                {
                    if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                        return;
                    if (sel.CardUidOrIndex != 0)
                        pickedUids[pickedCount++] = sel.CardUidOrIndex;
                }
                scratch.EndChoiceGroup();
                if (pickedCount > 0)
                    DiscardCardsAndDraw(ref s, ref cold, rules, scratch, pickedUids[..pickedCount], drawCount: 0);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                    GenerateShivsToHand(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Shivs", 2), upgraded: upgrade > 0, inky: false);
                break;
            }

            case HotCustomCardId.KnifeTrap:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    Span<ushort> shivUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
                    int shivCount = 0;
                    int exhEnd = s.ExhaustStart + s.ExhaustCount;
                    for (int i = s.ExhaustStart; i < exhEnd && shivCount < shivUids.Length; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].IsShivTag)
                            shivUids[shivCount++] = s.Cards[i].CardUid;
                    }
                    for (int k = 0; k < shivCount && !IsOverOrEnding(in s); k++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Exhaust, shivUids[k]);
                        if (idx < 0)
                            continue;
                        if (upgrade > 0)
                            UpgradeCard(ref s, rules, idx);
                        AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }
                break;

            case HotCustomCardId.LeadingStrike:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    GenerateShivsToHand(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Shivs", 1), upgraded: false, inky: false);
                break;

            case HotCustomCardId.Malaise:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    int x = Math.Max(0, (int)playedCard.CapturedEnergy) + (upgrade > 0 ? 1 : 0);
                    if (x > 0)
                    {
                        byte eSlot = (byte)(3 + targetEnemySlot);
                        ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.StrengthPower, -x, isCardSource: true);
                        ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.WeakPower, x, isCardSource: true);
                    }
                }
                break;

            case HotCustomCardId.MementoMori:
            {
                int lowSlot = PickLowestHpLivingEnemySlot(in s);
                if (lowSlot >= 0)
                    ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, lowSlot, baseDmg, 1, powered: true);
                break;
            }

            case HotCustomCardId.Mirage:
            {
                int blk = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, -1, energySpent);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, blk);
                break;
            }

            case HotCustomCardId.Neutralize:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot))
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.WeakPower, GetNamedVarInt(in playedCard, def, "WeakPower", magic > 0 ? magic : 1), isCardSource: true);
                break;

            case HotCustomCardId.Nightmare:
                if (s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        ushort nmId = rules.GetPowerDefId(HotPowerId.NightmarePower);
                        if (nmId != 0)
                        {
                            HotCard clone = s.Cards[idx];
                            clone.CardUid = 0;
                            clone.AfflId = 0;
                            clone.AfflAmt = 0;
                            clone.Flags &= ~(HotCardFlags.SingleTurnRetain | HotCardFlags.IsSlyThisTurn | HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe);
                            if ((clone.LocalKeywordsMask & HotCardKeywordMask.Sly) != 0)
                                clone.Flags |= HotCardFlags.IsSlyThisTurn;
                            clone.Flags |= HotCardFlags.IsClone;
                            clone.ExtraReplayThisTurn = 0;
                            int detachedIdx = AddDetachedCard(ref cold, in clone);
                            AddPowerInstance(ref s, rules, 0, 0, nmId, 3, aux0: detachedIdx, aux1: 0, auxCardUid: 0);
                        }
                    }
                }
                break;

            case HotCustomCardId.Outbreak:
            {
                int poison = GetNamedVarInt(in playedCard, def, "PoisonPower", magic > 0 ? magic : 9);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PoisonPower, poison, isCardSource: true);
                Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
                int tCount = SnapshotLivingEnemies(in s, targets);
                for (int t = 0; t < tCount && !IsOverOrEnding(in s); t++)
                    TriggerPoison(ref s, ref cold, rules, scratch, (byte)(3 + targets[t]));
                break;
            }

            case HotCustomCardId.PiercingWail:
            {
                int strLoss = GetNamedVarInt(in playedCard, def, "StrengthLoss", magic > 0 ? magic : 6);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PiercingWailPower, strLoss, isCardSource: true);
                break;
            }

            case HotCustomCardId.Predator:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, 2, isCardSource: true);
                break;

            case HotCustomCardId.Prepared:
            {
                int count = GetNamedVarInt(in playedCard, def, "Cards", magic > 0 ? magic : 1);
                DrawCards(ref s, ref cold, rules, scratch, count, fromHandDraw: false);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    int discCount = Math.Min(count, Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction));
                    Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                    for (int k = 0; k < discCount && pickedCount < s.HandCount; k++)
                    {
                        if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                            return;
                        if (sel.CardUidOrIndex != 0)
                            pickedUids[pickedCount++] = sel.CardUidOrIndex;
                    }
                    scratch.EndChoiceGroup();
                    if (pickedCount > 0)
                        DiscardCardsAndDraw(ref s, ref cold, rules, scratch, pickedUids[..pickedCount], drawCount: 0);
                }
                break;
            }

            case HotCustomCardId.ShadowStep:
            {
                int handBefore = s.HandCount;
                if (handBefore > 0 && !IsOverOrEnding(in s))
                {
                    int cap = Math.Min(handBefore, HotLimits.MaxStackAllocElements);
                    Span<ushort> handUids = stackalloc ushort[cap];
                    for (int i = 0; i < cap; i++)
                        handUids[i] = s.Cards[i].CardUid;
                    DiscardCardsAndDraw(ref s, ref cold, rules, scratch, handUids, drawCount: 0);
                    if (scratch.NeedsChoice)
                        return;
                }
                if (!IsOverOrEnding(in s))
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ShadowStepPower, 1, isCardSource: true);
                break;
            }

            case HotCustomCardId.Skewer:
            {
                int x = Math.Max(0, (int)playedCard.CapturedEnergy);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, x, powered: true);
                break;
            }

            case HotCustomCardId.Slice:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                break;

            case HotCustomCardId.Shiv:
                if (GetPowerAmountById(in s, rules, 0, HotPowerId.FanOfKnivesPower) > 0)
                    ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                else
                    ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                break;

            case HotCustomCardId.StormOfSteel:
            {
                int discarded = s.HandCount;
                if (discarded > 0)
                {
                    int cap = Math.Min(discarded, HotLimits.MaxStackAllocElements);
                    Span<ushort> handUids = stackalloc ushort[cap];
                    for (int i = 0; i < cap; i++)
                        handUids[i] = s.Cards[i].CardUid;
                    DiscardCardsAndDraw(ref s, ref cold, rules, scratch, handUids, drawCount: 0);
                    if (scratch.NeedsChoice)
                        return;
                    if (!IsOverOrEnding(in s))
                        GenerateShivsToHand(ref s, ref cold, rules, scratch, cap, upgraded: upgrade > 0, inky: false);
                }
                break;
            }

            case HotCustomCardId.Suppress:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot))
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.WeakPower, GetNamedVarInt(in playedCard, def, "WeakPower", magic > 0 ? magic : 3), isCardSource: true);
                break;

            case HotCustomCardId.Survivor:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        DiscardCard(ref s, ref cold, rules, scratch, idx);
                }
                break;

            case HotCustomCardId.TheHunt:
            {
                bool wasAlive = IsValidEnemyTarget(in s, targetEnemySlot);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (wasAlive && !scratch.NeedsChoice && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.TheHuntPower, 1, isCardSource: true);
                break;
            }

            case HotCustomCardId.Tracking:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.TrackingPower, 50, isCardSource: true);
                break;

            case HotCustomCardId.UpMySleeve:
                GenerateShivsToHand(ref s, ref cold, rules, scratch, magic > 0 ? magic : 3, upgraded: false, inky: false);
                if (!scratch.NeedsChoice)
                    CostAddThisCombat(ref s.Cards[s.PlayStart + playOffsetInPlay], ref cold, -1);
                break;

            case HotCustomCardId.WraithForm:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.IntangiblePower, GetNamedVarInt(in playedCard, def, "IntangiblePower", upgrade > 0 ? 3 : 2), isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.WraithFormPower, GetNamedVarInt(in playedCard, def, "WraithFormPower", 1), isCardSource: true);
                break;

            // ===================== DEFECT =====================
            case HotCustomCardId.AdaptiveStrike:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    HotCard copy = s.Cards[s.PlayStart + playOffsetInPlay];
                    copy.CardUid = s.NextCardUid++;
                    copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe);
                    copy.Flags |= HotCardFlags.IsClone;
                    CostSetThisCombat(ref copy, ref cold, 0);
                    AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Discard, -1, createdByPlayer: true);
                }
                break;
            }

            case HotCustomCardId.AllForOne:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                    int idx = s.DiscardStart;
                    int end = idx + s.DiscardCount;
                    while (idx < end && s.HandCount < maxHand)
                    {
                        ref readonly HotCard dc = ref s.Cards[idx];
                        CardType t = rules.Cards[dc.CardDefId].Type;
                        if ((dc.Flags & HotCardFlags.CostsX) == 0
                            && t is CardType.Attack or CardType.Skill or CardType.Power
                            && GetEffectiveEnergyCost(in s, in dc, cold, rules, isInPlayablePile: false) == 0)
                        {
                            HotPiles.MoveCard(ref s, idx, HotPileKind.Hand, s.HandCount);
                            // DiscardStart shifted right by 1, so idx now points to next card in Discard!
                            idx++;
                        }
                        else
                        {
                            idx++;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Barrage:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, s.OrbCount, powered: true);
                break;

            case HotCustomCardId.BiasedCognition:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FocusPower, GetNamedVarInt(in playedCard, def, "FocusPower", upgrade > 0 ? 5 : 4), isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BiasedCognitionPower, GetNamedVarInt(in playedCard, def, "BiasedCognitionPower", 1), isCardSource: true);
                break;

            case HotCustomCardId.BoostAway:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                {
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    if (dazedId != 0)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Discard, -1, false, true);
                }
                break;

            case HotCustomCardId.BulkUp:
                RemoveOrbSlots(ref s, GetNamedVarInt(in playedCard, def, "OrbSlots", 1));
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, GetNamedVarInt(in playedCard, def, "StrengthPower", magic > 0 ? magic : 2), isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, GetNamedVarInt(in playedCard, def, "DexterityPower", magic > 0 ? magic : 2), isCardSource: true);
                break;

            case HotCustomCardId.Capacitor:
                AddOrbSlots(ref s, GetNamedVarInt(in playedCard, def, "Repeat", magic > 0 ? magic : 2));
                break;

            case HotCustomCardId.Chaos:
                ChannelRandomOrb(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Repeat", upgrade > 0 ? 2 : 1));
                break;

            case HotCustomCardId.Chill:
            {
                int enemies = CountLivingEnemies(in s);
                for (int k = 0; k < enemies; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Frost);
                break;
            }

            case HotCustomCardId.Claw:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                {
                    int inc = GetNamedVarInt(in playedCard, def, "Increase", magic > 0 ? magic : 2);
                    int total = s.TotalCardCount;
                    for (int i = 0; i < total; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].CardId == HotCustomCardId.Claw)
                        {
                            s.Cards[i].Var0Delta = (short)(s.Cards[i].Var0Delta + inc);
                            s.Cards[i].CardPrivate += inc;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Compact:
            {
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                ushort fuelId = rules.GetCardDefId(HotCustomCardId.Fuel);
                if (!scratch.NeedsChoice && fuelId != 0)
                {
                    for (int i = 0; i < s.HandCount && !IsOverOrEnding(in s); i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Status)
                        {
                            TransformCardInPlace(ref s, ref cold, rules, scratch, i, fuelId, upgrade > 0);
                            if (scratch.NeedsChoice)
                                return;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.CompileDriver:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                {
                    int distinct = CountDistinctOrbTypes(in s);
                    if (distinct > 0)
                        DrawCards(ref s, ref cold, rules, scratch, distinct, fromHandDraw: false);
                }
                break;

            case HotCustomCardId.ConsumingShadow:
            {
                int rep = GetNamedVarInt(in playedCard, def, "Repeat", 2);
                for (int k = 0; k < rep; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Dark);
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ConsumingShadowPower, GetNamedVarInt(in playedCard, def, "ConsumingShadowPower", 1), isCardSource: true);
                break;
            }

            case HotCustomCardId.Darkness:
                ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Dark);
                if (!scratch.NeedsChoice)
                {
                    int triggers = upgrade > 0 ? 2 : 1;
                    int orbCount = s.OrbCount;
                    for (int o = 0; o < orbCount && o < s.OrbCount; o++)
                    {
                        if (GetOrbId(rules, s.Orbs[o].OrbTypeId) == HotOrbId.Dark)
                        {
                            for (int t = 0; t < triggers; t++)
                                TriggerOrbPassive(ref s, ref cold, rules, scratch, o, -1, applyTriggerCountMods: false);
                        }
                    }
                }
                break;

            case HotCustomCardId.DoubleEnergy:
                GainEnergy(ref s, rules, s.Energy);
                break;

            case HotCustomCardId.Dualcast:
                EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: 2, dequeue: true);
                break;

            case HotCustomCardId.FightThrough:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                {
                    ushort woundId = rules.GetCardDefId(HotCustomCardId.Wound);
                    if (woundId != 0)
                    {
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, woundId, HotPileKind.Discard, -1, false, true);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, woundId, HotPileKind.Discard, -1, false, true);
                    }
                }
                break;

            case HotCustomCardId.FlakCannon:
            {
                int statusExhausted = 0;
                int i = 0;
                while (i < s.ExhaustStart && !IsOverOrEnding(in s))
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Status)
                    {
                        ExhaustCard(ref s, ref cold, rules, scratch, i, causedByEthereal: false);
                        if (scratch.NeedsChoice)
                            return;
                        statusExhausted++;
                    }
                    else
                    {
                        i++;
                    }
                }
                ExecuteRandomEnemiesAttack(ref s, ref cold, rules, scratch, s.PlayStart + playOffsetInPlay, baseDmg, statusExhausted, powered: true);
                break;
            }

            case HotCustomCardId.Ftl:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] < GetNamedVarInt(in playedCard, def, "PlayMax", 3))
                    DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 1), fromHandDraw: false);
                break;

            case HotCustomCardId.GeneticAlgorithm:
            {
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                int curPlay = s.PlayStart + playOffsetInPlay;
                int inc = GetNamedVarInt(in playedCard, def, "Increase", magic > 0 ? magic : 2);
                s.Cards[curPlay].Var1Delta = (short)(s.Cards[curPlay].Var1Delta + inc);
                s.Cards[curPlay].CardPrivate += inc;
                break;
            }

            case HotCustomCardId.Glacier:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice)
                {
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Frost);
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Frost);
                }
                break;

            case HotCustomCardId.GoForTheEyes:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot) && IsEnemyIntendingToAttack(in s, rules, targetEnemySlot))
                    ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.WeakPower, GetNamedVarInt(in playedCard, def, "WeakPower", magic > 0 ? magic : 1), isCardSource: true);
                break;

            case HotCustomCardId.GunkUp:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                if (!scratch.NeedsChoice)
                {
                    ushort slimedId = rules.GetCardDefId(HotCustomCardId.Slimed);
                    if (slimedId != 0)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, slimedId, HotPileKind.Discard, -1, false, true);
                }
                break;

            case HotCustomCardId.HelixDrill:
            {
                int hHits = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, targetEnemySlot, energySpent);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hHits, powered: true);
                break;
            }

            case HotCustomCardId.Hibernate:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.HibernatePower, magic > 0 ? magic : 1, isCardSource: true);
                break;

            case HotCustomCardId.Hologram:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.DiscardCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.DiscardPile, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Discard, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                break;

            case HotCustomCardId.Hyperbeam:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.HyperbeamFocusDownPower, GetNamedVarInt(in playedCard, def, "FocusPower", magic > 0 ? magic : 3), isCardSource: true);
                break;

            case HotCustomCardId.IceLance:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                int rep = GetNamedVarInt(in playedCard, def, "Repeat", magic > 0 ? magic : 3);
                for (int k = 0; k < rep && !scratch.NeedsChoice; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Frost);
                break;
            }

            case HotCustomCardId.Ignition:
                ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Plasma);
                break;

            case HotCustomCardId.MeteorStrike:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                for (int k = 0; k < 3 && !scratch.NeedsChoice; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Plasma);
                break;

            case HotCustomCardId.Modded:
                AddOrbSlots(ref s, GetNamedVarInt(in playedCard, def, "Repeat", 1));
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 2 : 1), fromHandDraw: false);
                if (!scratch.NeedsChoice)
                    CostAddThisCombat(ref s.Cards[s.PlayStart + playOffsetInPlay], ref cold, +1);
                break;

            case HotCustomCardId.MomentumStrike:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    CostSetThisCombat(ref s.Cards[s.PlayStart + playOffsetInPlay], ref cold, 0);
                break;

            case HotCustomCardId.MultiCast:
            {
                int rep = Math.Max(0, (int)playedCard.CapturedEnergy) + (upgrade > 0 ? 1 : 0);
                if (rep > 0)
                    EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: rep, dequeue: true);
                break;
            }

            case HotCustomCardId.Overclock:
            {
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", magic > 0 ? magic : 2), fromHandDraw: false);
                ushort burnId = rules.GetCardDefId(HotCustomCardId.Burn);
                if (!scratch.NeedsChoice && burnId != 0)
                    AddGeneratedFixedCard(ref s, ref cold, rules, scratch, burnId, HotPileKind.Discard, -1, false, true);
                break;
            }

            case HotCustomCardId.Quadcast:
                EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: GetNamedVarInt(in playedCard, def, "Repeat", 4), dequeue: true);
                break;

            case HotCustomCardId.Reboot:
                while (s.HandCount > 0)
                    HotPiles.MoveCard(ref s, 0, HotPileKind.Draw, s.DrawCount);
                HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                {
                    HotHookArgs shArgs = new() { ActorSlot = 0 };
                    DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                    if (scratch.NeedsChoice)
                        return;
                }
                DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 4, fromHandDraw: false);
                break;

            case HotCustomCardId.Refract:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                int rep = GetNamedVarInt(in playedCard, def, "Repeat", 2);
                for (int k = 0; k < rep && !scratch.NeedsChoice; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Glass);
                break;
            }

            case HotCustomCardId.Scavenge:
                if (s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                }
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, GetNamedVarInt(in playedCard, def, "Energy", magic > 0 ? magic : 2), isCardSource: true);
                break;

            case HotCustomCardId.Scrape:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int handBefore = s.HandCount;
                    int drawn = DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 4, fromHandDraw: false);
                    if (scratch.NeedsChoice)
                        return;
                    Span<ushort> toDiscardUids = stackalloc ushort[16];
                    int discCount = 0;
                    for (int i = handBefore; i < s.HandCount && discCount < toDiscardUids.Length; i++)
                    {
                        ref readonly HotCard c = ref s.Cards[i];
                        if ((c.Flags & HotCardFlags.CostsX) != 0 || GetEffectiveEnergyCost(in s, in c, cold, rules) != 0)
                            toDiscardUids[discCount++] = c.CardUid;
                    }
                    if (discCount > 0)
                        DiscardCardsAndDraw(ref s, ref cold, rules, scratch, toDiscardUids[..discCount], drawCount: 0);
                }
                break;
            }

            case HotCustomCardId.Shatter:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                while (s.OrbCount > 0 && !scratch.NeedsChoice && !IsOverOrEnding(in s))
                    EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: 2, dequeue: true);
                break;

            case HotCustomCardId.Spinner:
                if (upgrade > 0)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Glass);
                if (!scratch.NeedsChoice)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SpinnerPower, GetNamedVarInt(in playedCard, def, "SpinnerPower", 1), isCardSource: true);
                break;

            case HotCustomCardId.Sunder:
            {
                bool wasAlive = IsValidEnemyTarget(in s, targetEnemySlot);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (wasAlive && !scratch.NeedsChoice && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", 3));
                break;
            }

            case HotCustomCardId.SweepingBeam:
                ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                break;

            case HotCustomCardId.Synchronize:
            {
                int synAmt = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, -1, energySpent);
                if (synAmt > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SynchronizePower, synAmt, isCardSource: true);
                break;
            }

            case HotCustomCardId.Tempest:
            {
                int x = Math.Max(0, (int)playedCard.CapturedEnergy) + (upgrade > 0 ? 1 : 0);
                for (int k = 0; k < x && !scratch.NeedsChoice; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                break;
            }

            case HotCustomCardId.TeslaCoil:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int triggers = upgrade > 0 ? 2 : 1;
                    int orbCount = s.OrbCount;
                    for (int o = 0; o < orbCount && o < s.OrbCount; o++)
                    {
                        if (GetOrbId(rules, s.Orbs[o].OrbTypeId) == HotOrbId.Lightning)
                        {
                            for (int t = 0; t < triggers; t++)
                                TriggerOrbPassive(ref s, ref cold, rules, scratch, o, targetEnemySlot, applyTriggerCountMods: false);
                        }
                    }
                }
                break;

            case HotCustomCardId.Turbo:
            {
                GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", upgrade > 0 ? 3 : 2));
                ushort voidId = rules.GetCardDefId(HotCustomCardId.Void);
                if (voidId != 0)
                    AddGeneratedFixedCard(ref s, ref cold, rules, scratch, voidId, HotPileKind.Discard, -1, false, true);
                break;
            }

            case HotCustomCardId.Uproar:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    Span<ushort> playableAtks = stackalloc ushort[HotLimits.MaxStackAllocElements];
                    Span<ushort> allAtks = stackalloc ushort[HotLimits.MaxStackAllocElements];
                    int pCount = 0, aCount = 0;
                    int drawEnd = s.DrawStart + s.DrawCount;
                    for (int i = s.DrawStart; i < drawEnd; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Attack)
                        {
                            if (aCount < allAtks.Length)
                                allAtks[aCount++] = s.Cards[i].CardUid;
                            if ((s.Cards[i].LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0 && pCount < playableAtks.Length)
                                playableAtks[pCount++] = s.Cards[i].CardUid;
                        }
                    }
                    // STS2 0.113.1 Uproar.cs:34-44: ToList().StableShuffle(CombatCardSelection).FirstOrDefault().
                    // Unconditionally consumes 1 NextULong on playableAtks, and a 2nd on allAtks when playableAtks is empty.
                    if (pCount > 1)
                        StableSortUidsForShuffle(in s, rules, cold, playableAtks[..pCount]);
                    HotRng.UnstableShuffle(playableAtks[..pCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                    ushort chosenUid = pCount > 0 ? playableAtks[0] : (ushort)0;
                    if (chosenUid == 0)
                    {
                        if (aCount > 1)
                            StableSortUidsForShuffle(in s, rules, cold, allAtks[..aCount]);
                        HotRng.UnstableShuffle(allAtks[..aCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                        if (aCount > 0)
                            chosenUid = allAtks[0];
                    }
                    if (chosenUid != 0)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, chosenUid);
                        if (idx >= 0)
                            AutoPlayCard(ref s, ref cold, rules, scratch, idx);
                    }
                }
                break;
            }

            case HotCustomCardId.Voltaic:
            {
                int count = s.HistoryLightningChannels;
                for (int k = 0; k < count && !scratch.NeedsChoice; k++)
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                break;
            }

            case HotCustomCardId.WhiteNoise:
                if (rules.CombatCharacterPowerPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[1];
                    if (HotPotions.TakeRandomDistinct(rules.CombatCharacterPowerPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0 && picked[0] != 0)
                        HotPotions.AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, picked[0]);
                }
                break;

            // ===================== COLORLESS / EVENT =====================
            case HotCustomCardId.Apotheosis:
            {
                ushort selfUid = playedCard.CardUid;
                int total = s.TotalCardCount;
                for (int i = 0; i < total; i++)
                {
                    if (s.Cards[i].CardUid != selfUid)
                        UpgradeCard(ref s, rules, i);
                }
                break;
            }

            case HotCustomCardId.BrightestFlame:
                GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", 2));
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 2), fromHandDraw: false);
                if (!scratch.NeedsChoice)
                    LoseMaxHp(ref s, ref cold, rules, scratch, 0, GetNamedVarInt(in playedCard, def, "MaxHp", 2));
                break;

            case HotCustomCardId.Coordinate:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.CoordinatePower, magic > 0 ? magic : 5, isCardSource: true);
                break;

            case HotCustomCardId.Discovery:
            {
                ushort[] pool = rules.UnlockedCharacterCardPool;
                if (pool.Length > 0)
                {
                    Span<ushort> rolled = stackalloc ushort[3];
                    int rolledCount = RollDistinctFromPool(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules, pool, rolled);
                    SetPendingGeneratedChoiceOptions(rolled[..rolledCount]);
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, playedCard.CardDefId, 0, 1, out HotChoiceSelection sel))
                        return;
                    if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                    {
                        int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sel.CardDefId, HotPileKind.Hand, -1, false, true);
                        if (idx >= 0)
                            SetCardToFreeThisTurn(ref s.Cards[idx], ref cold);
                    }
                }
                break;
            }

            case HotCustomCardId.Abundance:
            {
                ushort[] pool = rules.CombatCharacterPowerPool;
                if (pool.Length > 0)
                {
                    Span<ushort> rolled = stackalloc ushort[3];
                    int rolledCount = RollDistinctFromPool(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules, pool, rolled);
                    SetPendingGeneratedChoiceOptions(rolled[..rolledCount]);
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                    {
                        int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sel.CardDefId, HotPileKind.Hand, -1, upgraded: true, createdByPlayer: true);
                        if (idx >= 0)
                            SetCardToFreeThisTurn(ref s.Cards[idx], ref cold);
                    }
                }
                break;
            }

            case HotCustomCardId.Splash:
            {
                ushort[] otherPool = rules.OtherCharacterCardPool;
                if (otherPool.Length > 0)
                {
                    Span<ushort> otherAttacks = otherPool.Length <= 512
                        ? stackalloc ushort[otherPool.Length]
                        : new ushort[otherPool.Length];
                    int atkCount = 0;
                    for (int i = 0; i < otherPool.Length; i++)
                    {
                        ushort id = otherPool[i];
                        if (id != 0 && id < rules.Cards.Length && rules.Cards[id].Type == CardType.Attack && IsCombatGeneratableCard(rules.Cards[id]))
                            otherAttacks[atkCount++] = id;
                    }
                    if (atkCount > 0)
                    {
                        Span<ushort> work = otherAttacks[..atkCount];
                        HotRng.UnstableShuffle(work, ref s.Rng[(int)HotRngStream.CombatCardGeneration]);
                        int rolledCount = Math.Min(3, atkCount);
                        SetPendingGeneratedChoiceOptions(work[..rolledCount]);
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, playedCard.CardDefId, 0, 1, out HotChoiceSelection sel))
                            return;
                        if (sel.Kind == HotChoiceKind.GeneratedCard && sel.CardDefId != 0)
                        {
                            int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sel.CardDefId, HotPileKind.Hand, -1, upgraded: upgrade > 0, createdByPlayer: true);
                            if (idx >= 0)
                                SetCardToFreeThisTurn(ref s.Cards[idx], ref cold);
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Alchemize:
                if (HotPotions.TryProcureRandomCombatPotion(ref s, ref cold, rules, scratch))
                    s.Growth[4]++;
                break;

            case HotCustomCardId.Anointed:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int room = Math.Max(0, maxHand - s.HandCount);
                Span<ushort> rareUids = stackalloc ushort[HotLimits.MaxCards];
                int rareCount = 0;
                int drawStart = s.DrawStart;
                int drawEnd = drawStart + s.DrawCount;
                for (int i = drawStart; i < drawEnd; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Rarity == CardRarity.Rare)
                        rareUids[rareCount++] = s.Cards[i].CardUid;
                }
                // Matches IEnumerableExtensions.TakeRandom (UnstableShuffle always consumes 1 NextULong).
                HotRng.UnstableShuffle(rareUids[..rareCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                int take = Math.Min(room, rareCount);
                for (int i = 0; i < take && !IsOverOrEnding(in s); i++)
                {
                    int idx = FindCardIndexByUid(in s, HotPileKind.Draw, rareUids[i]);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                break;
            }

            case HotCustomCardId.BeatDown:
            {
                int count = GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 4 : 3);
                Span<ushort> atkUids = stackalloc ushort[HotLimits.MaxCards];
                int atkCount = 0;
                int discStart = s.DiscardStart;
                int discEnd = discStart + s.DiscardCount;
                for (int i = discStart; i < discEnd; i++)
                {
                    ref readonly HotCard c = ref s.Cards[i];
                    if (rules.Cards[c.CardDefId].Type == CardType.Attack
                        && (c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
                    {
                        atkUids[atkCount++] = c.CardUid;
                    }
                }
                StableSortUidsForShuffle(in s, rules, cold, atkUids[..atkCount]);
                HotRng.UnstableShuffle(atkUids[..atkCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                int playCount = Math.Min(count, atkCount);
                for (int i = 0; i < playCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                {
                    int idx = FindCardIndexByUid(in s, HotPileKind.Discard, atkUids[i]);
                    if (idx < 0)
                        continue;
                    HotCardDef targetDef = rules.Cards[s.Cards[idx].CardDefId];
                    int targetSlot = targetDef.Target == TargetType.AnyEnemy
                        ? PickRandomLivingEnemySlot(ref s)
                        : -1;
                    AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot: targetSlot, forceExhaust: false);
                }
                break;
            }

            case HotCustomCardId.Catastrophe:
            {
                int count = GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 3 : 2);
                Span<ushort> drawUids = stackalloc ushort[HotLimits.MaxCards];
                for (int i = 0; i < count && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                {
                    int playableCount = 0;
                    int drawStart = s.DrawStart;
                    int drawEnd = drawStart + s.DrawCount;
                    for (int d = drawStart; d < drawEnd; d++)
                    {
                        if ((s.Cards[d].LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
                            drawUids[playableCount++] = s.Cards[d].CardUid;
                    }
                    StableSortUidsForShuffle(in s, rules, cold, drawUids[..playableCount]);
                    HotRng.UnstableShuffle(drawUids[..playableCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                    ushort chosenUid = playableCount > 0 ? drawUids[0] : (ushort)0;
                    if (chosenUid == 0)
                    {
                        int allCount = 0;
                        drawStart = s.DrawStart;
                        drawEnd = drawStart + s.DrawCount;
                        for (int d = drawStart; d < drawEnd; d++)
                            drawUids[allCount++] = s.Cards[d].CardUid;
                        StableSortUidsForShuffle(in s, rules, cold, drawUids[..allCount]);
                        HotRng.UnstableShuffle(drawUids[..allCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                        if (allCount > 0)
                            chosenUid = drawUids[0];
                    }
                    if (chosenUid != 0)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, chosenUid);
                        if (idx >= 0)
                            AutoPlayCard(ref s, ref cold, rules, scratch, idx, targetEnemySlot: -1, forceExhaust: false);
                    }
                }
                break;
            }

            case HotCustomCardId.Distraction:
            {
                Span<ushort> pick = stackalloc ushort[1];
                int rolled = RollDistinctFromPool(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules, rules.CombatCharacterSkillPool, pick);
                if (rolled > 0 && pick[0] != 0)
                    HotPotions.AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, pick[0]);
                break;
            }

            case HotCustomCardId.HiddenGem:
            {
                if (s.DrawCount > 0)
                {
                    int replayAdd = GetNamedVarInt(in playedCard, def, "Replay", upgrade > 0 ? 3 : 2);
                    Span<int> eligible = stackalloc int[HotLimits.MaxCards];
                    Span<int> preferred = stackalloc int[HotLimits.MaxCards];
                    int eligCount = 0;
                    int prefCount = 0;
                    int drawStart = s.DrawStart;
                    int drawEnd = drawStart + s.DrawCount;
                    for (int i = drawStart; i < drawEnd; i++)
                    {
                        ref readonly HotCard c = ref s.Cards[i];
                        if ((c.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                            continue;
                        CardType cType = rules.Cards[c.CardDefId].Type;
                        if (cType is CardType.Curse or CardType.Quest)
                            continue;
                        if (GetEnchantedReplayCount(in c, rules) >= 1)
                            continue;
                        eligible[eligCount++] = i;
                        if (cType is CardType.Attack or CardType.Skill or CardType.Power)
                            preferred[prefCount++] = i;
                    }
                    ReadOnlySpan<int> poolSpan = prefCount > 0 ? preferred[..prefCount] : eligible[..eligCount];
                    if (poolSpan.Length > 0)
                    {
                        int chosenIdx = poolSpan[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], poolSpan.Length)];
                        s.Cards[chosenIdx].BaseReplayCount = (byte)Math.Min(byte.MaxValue, s.Cards[chosenIdx].BaseReplayCount + replayAdd);
                    }
                }
                break;
            }

            case HotCustomCardId.MadScience:
            {
                CardType msType = (CardType)(playedCard.CardPrivate & 0xFFFF);
                if (msType == CardType.None)
                    msType = def.Type;
                HotTinkerRiderEffect rider = (HotTinkerRiderEffect)((playedCard.CardPrivate >> 16) & 0xFFFF);

                switch (msType)
                {
                    case CardType.Attack:
                    {
                        int msHits = rider == HotTinkerRiderEffect.Violence
                            ? GetNamedVarInt(in playedCard, def, "ViolenceHits", 3)
                            : 1;
                        ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg > 0 ? baseDmg : 12, msHits, powered: true);
                        break;
                    }
                    case CardType.Skill:
                        GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk > 0 ? baseBlk : 8);
                        break;
                    case CardType.Power:
                        switch (rider)
                        {
                            case HotTinkerRiderEffect.Expertise:
                                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, GetNamedVarInt(in playedCard, def, "ExpertiseStrength", 2), isCardSource: true);
                                if (!scratch.NeedsChoice)
                                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, GetNamedVarInt(in playedCard, def, "ExpertiseDexterity", 2), isCardSource: true);
                                break;
                            case HotTinkerRiderEffect.Curious:
                                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.CuriousPower, GetNamedVarInt(in playedCard, def, "CuriousReduction", 1), isCardSource: true);
                                break;
                            case HotTinkerRiderEffect.Improvement:
                                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.ImprovementPower, 1, isCardSource: true);
                                s.Growth[9]++;
                                break;
                        }
                        break;
                }

                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    switch (rider)
                    {
                        case HotTinkerRiderEffect.Sapping:
                            if (IsValidEnemyTarget(in s, targetEnemySlot))
                            {
                                byte eSlot = (byte)(3 + targetEnemySlot);
                                ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.WeakPower, GetNamedVarInt(in playedCard, def, "SappingWeak", 2), isCardSource: true);
                                if (!scratch.NeedsChoice)
                                    ApplyPowerById(ref s, ref cold, rules, scratch, eSlot, 0, HotPowerId.VulnerablePower, GetNamedVarInt(in playedCard, def, "SappingVulnerable", 2), isCardSource: true);
                            }
                            break;
                        case HotTinkerRiderEffect.Choking:
                            if (IsValidEnemyTarget(in s, targetEnemySlot))
                                ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + targetEnemySlot), 0, HotPowerId.StranglePower, GetNamedVarInt(in playedCard, def, "ChokingDamage", 6), isCardSource: true);
                            break;
                        case HotTinkerRiderEffect.Energized:
                            GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "EnergizedEnergy", 2));
                            break;
                        case HotTinkerRiderEffect.Wisdom:
                            DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "WisdomCards", 3), fromHandDraw: false);
                            break;
                        case HotTinkerRiderEffect.Chaos:
                        {
                            Span<ushort> pick = stackalloc ushort[1];
                            int rolled = RollDistinctFromPool(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules, rules.UnlockedCharacterCardPool, pick);
                            if (rolled > 0 && pick[0] != 0)
                                HotPotions.AddGeneratedFreeThisTurnToHand(ref s, ref cold, rules, scratch, pick[0]);
                            break;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Metamorphosis:
            {
                int count = GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 5 : 3);
                ushort[] atkPool = rules.CombatCharacterAttackPool;
                if (count > 0 && atkPool.Length > 0)
                {
                    Span<ushort> rolled = count <= HotLimits.MaxStackAllocElements
                        ? stackalloc ushort[HotLimits.MaxStackAllocElements]
                        : new ushort[count];
                    for (int i = 0; i < count; i++)
                        rolled[i] = atkPool[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], atkPool.Length)];
                    for (int i = 0; i < count && !scratch.NeedsChoice && !IsOverOrEnding(in s); i++)
                    {
                        ushort cid = rolled[i];
                        HotCardDef cdef = rules.Cards[cid];
                        HotCard generated = new()
                        {
                            CardDefId = cid,
                            CardUid = s.NextCardUid++,
                            UpgradeLevel = 0,
                            BaseEnergyCost = cdef.BaseCost,
                            BaseStarCost = cdef.BaseStarCost,
                            LocalKeywordsMask = (HotCardKeywordMask)cdef.BaseKeywords,
                            Flags = GetInitialCardFlags(cdef),
                        };
                        HotPotions.SetCardFreeThisCombat(ref generated, ref cold);
                        AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in generated, HotPileKind.Draw, dstOffset: -2, createdByPlayer: true);
                    }
                }
                break;
            }

            case HotCustomCardId.MyShinies:
            {
                int hitCount = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, targetEnemySlot, energySpent);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg > 0 ? baseDmg : 1, hitCount, powered: true);
                break;
            }

            case HotCustomCardId.Jackpot:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int genCount = GetNamedVarInt(in playedCard, def, "Cards", magic > 0 ? magic : 3);
                    if (genCount > 0)
                    {
                        GenerateRandomFromPoolWithReplacement(
                            ref s,
                            ref cold,
                            rules,
                            scratch,
                            rules.UnlockedCharacterCardPool,
                            genCount,
                            upgraded: upgrade > 0,
                            zeroCostOnly: true);
                    }
                }
                break;
            }

            case HotCustomCardId.DualWield:
                if (HasEligibleHandCardForFilter(in s, rules, HotCustomCardId.DualWield))
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                    {
                        HotCard tpl = s.Cards[idx];
                        int copies = GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 2 : 1);
                        for (int k = 0; k < copies; k++)
                        {
                            HotCard copy = tpl;
                            copy.CardUid = s.NextCardUid++;
                            copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe);
                            copy.Flags |= HotCardFlags.IsClone;
                            AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Hand, -1, createdByPlayer: true);
                        }
                    }
                }
                break;

            case HotCustomCardId.Enlightenment:
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (upgrade > 0)
                        CostSetThisCombat(ref s.Cards[i], ref cold, 1, reduceOnly: true);
                    else
                        CostSetThisTurnOrUntilPlayed(ref s.Cards[i], ref cold, 1, reduceOnly: true);
                }
                break;

            case HotCustomCardId.Entrench:
                GainBlockInternal(ref s, ref cold, rules, scratch, 0, s.Player.Block, isCardBlock: true, cardIdx: livePlayIdx, cardDefId: playedCard.CardDefId, isUnpowered: true);
                break;

            case HotCustomCardId.FeedingFrenzy:
            {
                int str = GetNamedVarInt(in playedCard, def, "StrengthPower", magic > 0 ? magic : 5);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.FeedingFrenzyPower, str, isCardSource: true);
                break;
            }

            case HotCustomCardId.Fisticuffs:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                int dealt = t_lastAttackTotalPlusOverkill;
                if (!scratch.NeedsChoice && dealt > 0)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, dealt);
                break;
            }

            case HotCustomCardId.FlashOfSteel:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice)
                    DrawCards(ref s, ref cold, rules, scratch, 1, fromHandDraw: false);
                break;

            case HotCustomCardId.HandOfGreed:
            {
                bool wasAlive = IsValidEnemyTarget(in s, targetEnemySlot);
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (wasAlive && !scratch.NeedsChoice && (s.Enemies[targetEnemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    s.Gold += GetNamedVarInt(in playedCard, def, "Gold", magic > 0 ? magic : 20);
                break;
            }

            case HotCustomCardId.HuddleUp:
                DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 2, fromHandDraw: false);
                break;

            case HotCustomCardId.Impatience:
            {
                bool hasAttack = false;
                for (int i = 0; i < s.HandCount; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Attack)
                    {
                        hasAttack = true;
                        break;
                    }
                }
                if (!hasAttack)
                    DrawCards(ref s, ref cold, rules, scratch, magic > 0 ? magic : 2, fromHandDraw: false);
                break;
            }

            case HotCustomCardId.JackOfAllTrades:
            {
                ushort[] colorlessPool = rules.CombatColorlessPool.Length > 0
                    ? rules.CombatColorlessPool
                    : rules.UnlockedColorlessCardPool;
                if (colorlessPool.Length > 0)
                {
                    int cnt = GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 2 : 1);
                    if (cnt > 0)
                    {
                        Span<ushort> rolled = cnt <= 16 ? stackalloc ushort[16] : new ushort[cnt];
                        int got = RollDistinctFromPool(
                            ref s.Rng[(int)HotRngStream.CombatCardGeneration],
                            rules,
                            colorlessPool,
                            rolled[..cnt],
                            excludeCardId: HotCustomCardId.JackOfAllTrades);
                        for (int k = 0; k < got && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rolled[k], HotPileKind.Hand, -1, false, true);
                    }
                }
                break;
            }

            case HotCustomCardId.FranticEscape:
            {
                ushort sandpitId = rules.GetPowerDefId(HotPowerId.SandpitPower);
                if (sandpitId != 0)
                {
                    int rosterCount = s.RosterCount;
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte eSlot = s.ActiveEnemyRoster[r];
                        if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                            continue;
                        byte enemyCreatureSlot = (byte)(3 + eSlot);
                        int pCount = s.PowerCount;
                        bool found = false;
                        for (int p = 0; p < pCount; p++)
                        {
                            if (s.Powers[p].OwnerSlot == enemyCreatureSlot
                                && s.Powers[p].PowerDefId == sandpitId
                                && s.Powers[p].Amount > 0
                                && s.Powers[p].TargetSlot == 0)
                            {
                                s.Powers[p].Amount += 1;
                                found = true;
                                break;
                            }
                        }
                        if (found)
                            break;
                    }
                }
                CostAddThisCombat(ref s.Cards[s.PlayStart + playOffsetInPlay], ref cold, +1);
                break;
            }

            case HotCustomCardId.Maul:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                if (!scratch.NeedsChoice)
                {
                    int inc = GetNamedVarInt(in playedCard, def, "Increase", magic > 0 ? magic : 1);
                    int total = s.TotalCardCount;
                    for (int i = 0; i < total; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].CardId == HotCustomCardId.Maul)
                        {
                            s.Cards[i].Var0Delta = (short)(s.Cards[i].Var0Delta + inc);
                            s.Cards[i].CardPrivate += inc;
                        }
                    }
                }
                break;
            }

            case HotCustomCardId.Mimic:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, s.Enemies[targetEnemySlot].Vital.Block);
                break;

            case HotCustomCardId.NeowsFury:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.DiscardCount > 0)
                {
                    int maxPick = Math.Min(
                        GetNamedVarInt(in playedCard, def, "Cards", 2),
                        Math.Min(Math.Max(0, (s.MaxHandSize > 0 ? s.MaxHandSize : 10) - s.HandCount), Math.Min((int)s.DiscardCount, HotLimits.MaxChoicesPerAction)));
                    Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.Optional | HotChoiceGroupFlags.MultiPick);
                    for (int k = 0; k < maxPick && pickedCount < s.DiscardCount; k++)
                    {
                        if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.DiscardPile, playedCard.CardDefId, 0, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                            return;
                        if (sel.Kind == HotChoiceKind.None || sel.CardUidOrIndex == 0)
                            break;
                        pickedUids[pickedCount++] = sel.CardUidOrIndex;
                    }
                    scratch.EndChoiceGroup();
                    for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Discard, pickedUids[i]);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                break;
            }

            case HotCustomCardId.Omnislice:
                if (IsValidEnemyTarget(in s, targetEnemySlot))
                {
                    ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                    int dealt = t_lastAttackTotalPlusOverkill;
                    if (!scratch.NeedsChoice && dealt > 0)
                    {
                        Span<byte> others = stackalloc byte[HotLimits.MaxEnemies];
                        int cnt = SnapshotLivingEnemies(in s, others);
                        for (int t = 0; t < cnt; t++)
                        {
                            if (others[t] != targetEnemySlot)
                                DealDamage(ref s, ref cold, rules, scratch, 0, (byte)(3 + others[t]), dealt, isPoweredAttack: false);
                        }
                    }
                }
                break;

            case HotCustomCardId.Prolong:
                if (s.Player.Block > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.BlockNextTurnPower, s.Player.Block, isCardSource: true);
                break;

            case HotCustomCardId.Prowess:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, GetNamedVarInt(in playedCard, def, "StrengthPower", magic > 0 ? magic : 1), isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DexterityPower, GetNamedVarInt(in playedCard, def, "DexterityPower", magic > 0 ? magic : 1), isCardSource: true);
                break;

            case HotCustomCardId.Purity:
            {
                int maxExh = Math.Min(GetNamedVarInt(in playedCard, def, "Cards", upgrade > 0 ? 5 : 3), Math.Min((int)s.HandCount, HotLimits.MaxChoicesPerAction));
                Span<ushort> pickedUids = stackalloc ushort[HotLimits.MaxChoicesPerAction];
                int pickedCount = 0;
                scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.Optional | HotChoiceGroupFlags.MultiPick);
                for (int k = 0; k < maxExh && pickedCount < s.HandCount; k++)
                {
                    if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 0, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                        return;
                    if (sel.Kind == HotChoiceKind.None || sel.CardUidOrIndex == 0)
                        break;
                    pickedUids[pickedCount++] = sel.CardUidOrIndex;
                }
                scratch.EndChoiceGroup();
                for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                {
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, pickedUids[i]);
                    if (idx >= 0)
                        ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                }
                break;
            }

            case HotCustomCardId.Relax:
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, GetNamedVarInt(in playedCard, def, "Cards", 2), isCardSource: true);
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.EnergyNextTurnPower, GetNamedVarInt(in playedCard, def, "Energy", 2), isCardSource: true);
                break;

            case HotCustomCardId.Restlessness:
                if (s.HandCount == 0)
                {
                    DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 2), fromHandDraw: false);
                    GainEnergy(ref s, rules, GetNamedVarInt(in playedCard, def, "Energy", 2));
                }
                break;

            case HotCustomCardId.RollingBoulder:
            {
                ushort rbId = rules.GetPowerDefId(HotPowerId.RollingBoulderPower);
                if (rbId != 0)
                {
                    int initDmg = GetNamedVarInt(in playedCard, def, "RollingBoulderPower", baseDmg > 0 ? baseDmg : 5);
                    int incDmg = GetNamedVarInt(in playedCard, def, "IncrementAmount", 5);
                    int pIdx = FindPowerIndex(in s, 0, rbId);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.RollingBoulderPower, initDmg, isCardSource: true);
                    pIdx = FindPowerIndex(in s, 0, rbId);
                    if (pIdx >= 0 && s.Powers[pIdx].Aux0 == 0)
                        s.Powers[pIdx].Aux0 = incDmg;
                }
                break;
            }

            case HotCustomCardId.Scrawl:
            {
                int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
                int need = maxHand - s.HandCount;
                if (need > 0)
                    DrawCards(ref s, ref cold, rules, scratch, need, fromHandDraw: false);
                break;
            }

            case HotCustomCardId.SecretTechnique:
            case HotCustomCardId.SecretWeapon:
            case HotCustomCardId.Wish:
                if (HasEligibleDrawCardForFilter(in s, rules, cardId))
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
                }
                break;

            case HotCustomCardId.SeekerStrike:
            {
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s))
                {
                    int drawStart = s.DrawStart;
                    int drawCount = s.DrawCount;
                    int takeCount = Math.Min(GetNamedVarInt(in playedCard, def, "Cards", 3), drawCount);
                    Span<int> indices = drawCount <= HotLimits.MaxStackAllocElements
                        ? stackalloc int[HotLimits.MaxStackAllocElements]
                        : new int[drawCount];
                    Span<int> drawIndices = indices[..drawCount];
                    for (int i = 0; i < drawCount; i++)
                        drawIndices[i] = drawStart + i;

                    // STS2 0.113.1 SeekerStrike.cs:36: DrawPile.Cards.ToList().StableShuffle(CombatCardSelection).Take(Cards)
                    // Unconditionally consumes 1 NextULong even when drawCount == 0.
                    for (int i = 1; i < drawCount; i++)
                    {
                        int keyIdx = drawIndices[i];
                        int j = i - 1;
                        while (j >= 0 && rules.CompareCardsForShuffle(in s.Cards[drawIndices[j]], in s.Cards[keyIdx], in s, cold) > 0)
                        {
                            drawIndices[j + 1] = drawIndices[j];
                            j--;
                        }
                        drawIndices[j + 1] = keyIdx;
                    }
                    HotRng.UnstableShuffle(drawIndices, ref s.Rng[(int)HotRngStream.CombatCardSelection]);

                    if (takeCount > 0)
                    {
                        Span<ushort> allowedUids = takeCount <= HotLimits.MaxStackAllocElements
                            ? stackalloc ushort[HotLimits.MaxStackAllocElements]
                            : new ushort[takeCount];
                        for (int i = 0; i < takeCount; i++)
                            allowedUids[i] = s.Cards[drawIndices[i]].CardUid;

                        if (!RequestChoiceFiltered(ref s, scratch, HotChoiceKind.DrawPile, playedCard.CardDefId, 1, 1, allowedUids[..takeCount], out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                break;
            }

            case HotCustomCardId.Shockwave:
            {
                int pAmt = GetNamedVarInt(in playedCard, def, "Power", magic > 0 ? magic : 3);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.WeakPower, pAmt, isCardSource: true);
                ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.VulnerablePower, pAmt, isCardSource: true);
                break;
            }

            case HotCustomCardId.Slimed:
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 1), fromHandDraw: false);
                break;

            case HotCustomCardId.Stack:
            {
                int blk = EvaluateCalcVar(in s, rules, scratch, in playedCard, def, -1, energySpent);
                GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, blk);
                break;
            }

            case HotCustomCardId.TheBomb:
            {
                ushort bombId = rules.GetPowerDefId(HotPowerId.TheBombPower);
                if (bombId != 0)
                {
                    int turns = GetNamedVarInt(in playedCard, def, "Turns", 3);
                    int bombDmg = GetNamedVarInt(in playedCard, def, "BombDamage", upgrade > 0 ? 50 : 40);
                    AddPowerInstance(ref s, rules, 0, 0, bombId, turns, aux0: bombDmg);
                }
                break;
            }

            case HotCustomCardId.ThinkingAhead:
                DrawCards(ref s, ref cold, rules, scratch, GetNamedVarInt(in playedCard, def, "Cards", 2), fromHandDraw: false);
                if (!scratch.NeedsChoice && !IsOverOrEnding(in s) && s.HandCount > 0)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, playedCard.CardDefId, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                    if (idx >= 0)
                        HotPiles.MoveCard(ref s, idx, HotPileKind.Draw, 0);
                }
                break;

            case HotCustomCardId.Volley:
            {
                int x = Math.Max(0, (int)playedCard.CapturedEnergy);
                ExecuteRandomEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, x, powered: true);
                break;
            }

            case HotCustomCardId.Whistle:
                ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, 1, powered: true);
                if (!scratch.NeedsChoice && IsValidEnemyTarget(in s, targetEnemySlot))
                    HotMonsters.ForceStunnedMove(ref s, rules, (byte)targetEnemySlot, null);
                break;

            default:
                // Generic fallback for unmapped basic attack/block/draw cards
                if (baseBlk > 0)
                    GainPlayedCardBlock(ref s, ref cold, rules, scratch, playOffsetInPlay, playedCard.CardDefId, baseBlk);
                if (baseDmg > 0)
                {
                    TargetType effectiveTarget = GetEffectiveCardTargetType(in s, in playedCard, def, rules);
                    if (effectiveTarget == TargetType.AllEnemies)
                        ExecuteAllEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, hits, powered: true);
                    else if (effectiveTarget == TargetType.RandomEnemy)
                        ExecuteRandomEnemiesAttack(ref s, ref cold, rules, scratch, livePlayIdx, baseDmg, hits, powered: true);
                    else if (IsValidEnemyTarget(in s, targetEnemySlot))
                        ExecuteSingleTargetAttack(ref s, ref cold, rules, scratch, livePlayIdx, targetEnemySlot, baseDmg, hits, powered: true);
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GainPlayedCardBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int playOffsetInPlay,
        ushort cardDefId,
        int baseAmount)
    {
        int cardIdx = playOffsetInPlay >= 0 && playOffsetInPlay < s.PlayCount
            ? s.PlayStart + playOffsetInPlay
            : -1;
        return GainBlockInternal(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: baseAmount, isCardBlock: true, cardIdx: cardIdx, cardDefId: cardDefId);
    }

    internal static void GenerateShivsToHand(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int count,
        bool upgraded,
        bool inky)
    {
        ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
        if (shivId == 0 || count <= 0)
            return;
        byte inkyEnchId = inky ? rules.GetEnchantmentDefId(HotEnchantmentId.Inky) : (byte)0;
        for (int k = 0; k < count; k++)
        {
            int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, -1, upgraded, createdByPlayer: true);
            if (idx >= 0 && inkyEnchId != 0)
            {
                s.Cards[idx].EnchId = inkyEnchId;
                s.Cards[idx].EnchAmt = 1;
            }
        }
    }

    internal static void TransformCardInPlace(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        int cardIdx,
        ushort newCardDefId,
        bool upgraded)
        => TransformCardInPlace(ref s, ref cold, rules, scratch: null, cardIdx, newCardDefId, upgraded);

    internal static void TransformCardInPlace(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch? scratch,
        int cardIdx,
        ushort newCardDefId,
        bool upgraded)
    {
        if (cardIdx < 0 || cardIdx >= s.TotalCardCount || newCardDefId == 0 || newCardDefId >= rules.Cards.Length)
            return;
        HotCardDef newDef = rules.Cards[newCardDefId];
        bool doUpgrade = upgraded && newDef.MaxUpgradeLevel > 0;
        HotCard transformed = new()
        {
            CardDefId = newCardDefId,
            CardUid = s.NextCardUid++,
            UpgradeLevel = doUpgrade ? (byte)1 : (byte)0,
            BaseEnergyCost = doUpgrade ? newDef.UpgradedCost : newDef.BaseCost,
            BaseStarCost = newDef.BaseStarCost,
            LocalKeywordsMask = (HotCardKeywordMask)newDef.BaseKeywords,
            Flags = GetInitialCardFlags(newDef),
        };
        if (doUpgrade)
            ApplyUpgradeKeywords(ref transformed, newDef.CardId);
        s.Cards[cardIdx] = transformed;
        HotPiles.MaybeGainArrowSlotOnPileEnter(ref s, newCardDefId, rules);
        s.HistoryCardsGenerated++;
        if (newDef.CardId == HotCustomCardId.Sadness)
            s.Counters[(int)HotCounterSlot.SadnessGeneratedBefore]++;
        if (newDef.HookMask != HotHookMask.None)
            s.ActiveHookMask |= (ulong)newDef.HookMask;

        if (scratch != null && (s.ActiveHookMask & (ulong)HotHookMask.AfterCardEnteredCombat) != 0)
        {
            HotHookArgs enterArgs = new()
            {
                ActorSlot = 0,
                CardIdx = cardIdx,
                CardDefId = newCardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardEnteredCombat, ref enterArgs);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCombatGeneratableCard(HotCardDef def)
        => def.CanBeGeneratedInCombat
            && def.Rarity != CardRarity.Basic
            && def.Rarity != CardRarity.Ancient
            && def.Rarity != CardRarity.Event;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsEnergyCostXCard(HotCustomCardId cardId)
        => cardId is HotCustomCardId.Brace
            or HotCustomCardId.Cascade
            or HotCustomCardId.Dirge
            or HotCustomCardId.Eradicate
            or HotCustomCardId.HeavenlyDrill
            or HotCustomCardId.Malaise
            or HotCustomCardId.MultiCast
            or HotCustomCardId.RepeatShot
            or HotCustomCardId.Skewer
            or HotCustomCardId.Tempest
            or HotCustomCardId.Volley
            or HotCustomCardId.Whirlwind;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsStarCostXCard(HotCustomCardId cardId)
        => cardId == HotCustomCardId.Stardust;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static HotCardFlags GetInitialCardFlags(HotCardDef def)
    {
        HotCardFlags flags = HotCardFlags.None;
        if (IsEnergyCostXCard(def.CardId))
            flags |= HotCardFlags.CostsX;
        if (IsStarCostXCard(def.CardId))
            flags |= HotCardFlags.HasStarCostX;
        if (((HotCardKeywordMask)def.BaseKeywords & HotCardKeywordMask.Sly) != 0)
            flags |= HotCardFlags.IsSlyThisTurn;
        return flags;
    }

    private static void GenerateRandomFromPoolWithReplacement(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort[] pool,
        int count,
        bool upgraded,
        bool zeroCostOnly)
    {
        if (count <= 0 || pool.Length == 0)
            return;

        Span<ushort> filtered = pool.Length <= 512
            ? stackalloc ushort[pool.Length]
            : new ushort[pool.Length];
        int filteredCount = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            ushort id = pool[i];
            if (id == 0 || id >= rules.Cards.Length)
                continue;
            HotCardDef d = rules.Cards[id];
            if (!IsCombatGeneratableCard(d))
                continue;
            if (zeroCostOnly && (d.BaseCost != 0 || IsEnergyCostXCard(d.CardId)))
                continue;
            filtered[filteredCount++] = id;
        }

        if (filteredCount == 0)
            return;

        // Roll all cards from CombatCardGeneration first (matching CardFactory.GetForCombat),
        // then add each generated card to Hand in order.
        Span<ushort> rolled = count <= HotLimits.MaxStackAllocElements
            ? stackalloc ushort[HotLimits.MaxStackAllocElements]
            : new ushort[count];
        for (int i = 0; i < count; i++)
            rolled[i] = filtered[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], filteredCount)];

        for (int i = 0; i < count && !scratch.NeedsChoice && !IsOverOrEnding(in s); i++)
            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rolled[i], HotPileKind.Hand, dstOffset: -1, upgraded: upgraded, createdByPlayer: true);
    }

    private static int RollDistinctFromPool(
        ref HotRngState rng,
        HotRules rules,
        ushort[] pool,
        Span<ushort> dst,
        HotCustomCardId excludeCardId = HotCustomCardId.None)
    {
        if (pool.Length == 0 || dst.IsEmpty)
            return 0;

        Span<ushort> filtered = pool.Length <= 512
            ? stackalloc ushort[pool.Length]
            : new ushort[pool.Length];
        int filteredCount = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            ushort id = pool[i];
            if (id == 0 || id >= rules.Cards.Length)
                continue;
            HotCardDef d = rules.Cards[id];
            if (!IsCombatGeneratableCard(d))
                continue;
            if (excludeCardId != HotCustomCardId.None && d.CardId == excludeCardId)
                continue;
            filtered[filteredCount++] = id;
        }

        if (filteredCount == 0)
            return 0;

        // Exact vanilla IEnumerableExtensions.TakeRandom: UnstableShuffle(rng) then Take(count).
        Span<ushort> work = filtered[..filteredCount];
        HotRng.UnstableShuffle(work, ref rng);
        int written = Math.Min(dst.Length, filteredCount);
        work[..written].CopyTo(dst);
        return written;
    }

    private static int AddDetachedCard(ref HotColdSidecar? cold, in HotCard card)
    {
        HotCard[]? existing = cold?.DetachedCards;
        int len = existing?.Length ?? 0;
        HotCard[] next = new HotCard[len + 1];
        if (existing != null && len > 0)
            existing.CopyTo(next, 0);
        next[len] = card;
        cold = cold == null
            ? new HotColdSidecar(DetachedCards: next)
            : cold with { DetachedCards = next };
        return len;
    }

    internal static void StableSortUidsForShuffle(
        in CombatHotState s,
        HotRules rules,
        HotColdSidecar? cold,
        Span<ushort> uids)
    {
        for (int i = 1; i < uids.Length; i++)
        {
            ushort keyUid = uids[i];
            int keyIdx = FindCardIndexByUidAnyPile(in s, keyUid);
            if (keyIdx < 0)
                continue;
            int j = i - 1;
            while (j >= 0)
            {
                int prevIdx = FindCardIndexByUidAnyPile(in s, uids[j]);
                if (prevIdx < 0 || rules.CompareCardsForShuffle(in s.Cards[prevIdx], in s.Cards[keyIdx], in s, cold) <= 0)
                    break;
                uids[j + 1] = uids[j];
                j--;
            }
            uids[j + 1] = keyUid;
        }
    }

    private static bool HasAnyUpgradableCardInHand(in CombatHotState s, HotRules rules)
    {
        for (int i = 0; i < s.HandCount; i++)
        {
            if (s.Cards[i].UpgradeLevel < rules.Cards[s.Cards[i].CardDefId].MaxUpgradeLevel)
                return true;
        }
        return false;
    }

    private static bool HasEligibleHandCardForFilter(in CombatHotState s, HotRules rules, HotCustomCardId source)
    {
        for (int i = 0; i < s.HandCount; i++)
        {
            if (MatchesChoiceFilter(in s, in s.Cards[i], rules, source))
                return true;
        }
        return false;
    }

    private static bool HasEligibleDrawCardForFilter(in CombatHotState s, HotRules rules, HotCustomCardId source)
    {
        int end = s.DrawStart + s.DrawCount;
        for (int i = s.DrawStart; i < end; i++)
        {
            if (MatchesChoiceFilter(in s, in s.Cards[i], rules, source))
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEnemyIntendingToAttack(in CombatHotState s, HotRules rules, int enemySlot)
    {
        if (!IsValidEnemyTarget(in s, enemySlot))
            return false;
        short nodeId = s.Enemies[enemySlot].CurrentMoveNode;
        HotMonsterMachine? m = rules.GetMonsterMachine(s.Enemies[enemySlot].MonsterDefId);
        if (m == null || nodeId < 0 || nodeId >= m.Nodes.Length)
            return false;
        HotMonsterNode node = m.Nodes[nodeId];
        return node.Repeats > 0 || (node.Attacks is { Length: > 0 });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountLivingEnemies(in CombatHotState s)
    {
        int alive = 0;
        for (int r = 0; r < s.RosterCount; r++)
        {
            if ((s.Enemies[s.ActiveEnemyRoster[r]].Vital.Flags & HotCreatureFlags.Alive) != 0)
                alive++;
        }
        return alive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasNamedVar(HotCardDef def, string name)
        => FindNamedVarIndex(def.OrderedVarNames, name) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static decimal GetNamedVarDecimal(in HotCard card, HotCardDef def, string name, decimal fallback)
    {
        int idx = FindNamedVarIndex(def.OrderedVarNames, name);
        return idx >= 0 ? HotRules.GetCardVarValue(in card, def, idx) : fallback;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetNamedVarInt(in HotCard card, HotCardDef def, string name, int fallback)
        => (int)Math.Truncate(GetNamedVarDecimal(in card, def, name, fallback));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNamedVarIndex(string[] sortedNames, string target)
    {
        int lo = 0;
        int hi = sortedNames.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int cmp = string.CompareOrdinal(sortedNames[mid], target);
            if (cmp == 0)
                return mid;
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return -1;
    }

    // Mirrors MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect (0.113.1) without importing Core.Models into Sim.
    private enum HotTinkerRiderEffect
    {
        None = 0,
        Sapping = 1,
        Violence = 2,
        Choking = 3,
        Energized = 4,
        Wisdom = 5,
        Chaos = 6,
        Expertise = 7,
        Curious = 8,
        Improvement = 9,
    }
}
