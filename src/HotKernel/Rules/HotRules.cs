using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.HotKernel;

/// <summary>
/// Root-frozen immutable rule tables, bytecode ops, compiled monster machines, active relic hook routing,
/// and Phase 1 coverage manifest shared across all search branches of a combat root.
/// </summary>
internal sealed class HotRules
{
    public required HotCardDef[] Cards { get; init; }
    public required HotPowerDef[] Powers { get; init; }
    public required HotRelicDef[] Relics { get; init; }
    public required HotPotionDef[] Potions { get; init; }
    public required HotOrbDef[] Orbs { get; init; }
    public required HotEnchantmentDef[] Enchantments { get; init; }
    public required HotAfflictionDef[] Afflictions { get; init; }
    public required HotMonsterDef[] Monsters { get; init; }

    public required HotCardEffectOp[] EffectOps { get; init; }
    public HotCardEffectOp[] PotionEffectOps { get; init; } = [];
    public required HotMonsterMachine[] MonsterMachines { get; init; }

    // Dense O(1) enum-ID -> per-root DefId tables (indexed by Hot*Id enum value; 0 = unregistered)
    public ushort[] CardDefIdByCardId { get; init; } = [];
    public ushort[] PowerDefIdByPowerId { get; init; } = [];
    public ushort[] RelicDefIdByRelicId { get; init; } = [];
    public byte[] RelicStateSlotByRelicId { get; init; } = [];
    public bool[] HasActiveRelicByRelicId { get; init; } = [];
    public ushort[] PotionDefIdByPotionId { get; init; } = [];
    public ushort[] MonsterDefIdByMonsterId { get; init; } = [];
    public byte[] OrbDefIdByOrbId { get; init; } = [];
    public byte[] EnchDefIdByEnchId { get; init; } = [];
    public byte[] AfflDefIdByAfflId { get; init; } = [];

    // Frozen vanilla combat generation / transformation pools (CardDefId / PotionDefId arrays)
    public ushort[] UnlockedCharacterCardPool { get; init; } = [];
    public ushort[] UnlockedColorlessCardPool { get; init; } = [];
    public ushort[] UnlockedCurseCardPool { get; init; } = [];
    public ushort[] UnlockedStatusCardPool { get; init; } = [];
    public ushort[] UnlockedTokenCardPool { get; init; } = [];
    public ushort[] OtherCharacterCardPool { get; init; } = [];
    public ushort[] TarotCardPool { get; init; } = [];
    public ushort[] CombatCharacterAttackPool { get; init; } = [];
    public ushort[] CombatCharacterSkillPool { get; init; } = [];
    public ushort[] CombatCharacterPowerPool { get; init; } = [];
    public ushort[] CombatColorlessPool { get; init; } = [];
    public ushort[] UnlockedPotionPool { get; init; } = [];
    /// <summary>
    /// Per-character-domain unlocked combat card pools indexed by <c>(int)HotCardDomain</c>
    /// (1=Ironclad .. 6=Fool), preserving exact <c>CardPoolModel.GetUnlockedCards</c> order.
    /// </summary>
    public ushort[][] UnlockedCharacterPoolsByDomain { get; init; } = [];

    // Encounter & search pruning metadata
    public HotDominionId Dominion { get; init; }
    public bool HasAnyHealOrUnknownGeneration { get; init; }
    public bool HasRenewablePotionShapedRock { get; init; }
    public bool IsFurCoatActiveInCurrentRoom { get; init; }
    public bool IsBossEncounter { get; init; }
    public bool IsEliteEncounter { get; init; }
    public bool IsWeakEncounter { get; init; }
    public bool IsTheftEncounter { get; init; }
    public string CharacterEntry { get; init; } = string.Empty;
    public string EncounterEntry { get; init; } = string.Empty;
    public string[] EncounterSlots { get; init; } = [];
    public int InitialEncounterMonsterCount { get; init; }
    public int CurrentActIndex { get; init; }
    public int TotalFloor { get; init; }
    public int PostCombatRelicUnconditionalHeal { get; init; }
    public int PostCombatRelicWoundedHeal { get; init; }
    public int PostCombatRelicWoundedHpPercent { get; init; }
    public int MadScienceUpgradeCapacity { get; init; } = int.MaxValue;

    public required ushort[] ActiveRelicDefIds { get; init; }
    public required HotActiveRelic[] ActiveRelics { get; init; }
    public required byte[] StatefulRelicOrder { get; init; }
    public required byte[] TrackedRelicOrder { get; init; }
    public required byte[][] RelicHooksByBit { get; init; }
    public required IReadOnlyDictionary<ushort, byte> RelicDefIdToStateSlot { get; init; }
    public required ulong StaticRelicHookMask { get; init; }

    public required HotCombatRulesSnapshot RulesSnapshot { get; init; }
    public GameMode GameMode => RulesSnapshot.GameMode;
    public int AscensionLevel => RulesSnapshot.AscensionLevel;
    public ulong AscensionsMask => RulesSnapshot.Ascensions;
    public string? DominionId => RulesSnapshot.DominionId;
    public bool HasMurderousModifier => RulesSnapshot.HasMurderousModifier;
    public required byte MaxHandSize { get; init; }
    public required bool EncounterGoldForfeited { get; init; }
    public required int BaseMaxEnergy { get; init; }
    public required int BaseHandDraw { get; init; }
    public byte BaseOrbSlotCount { get; init; }

    public required string[] EnemySlotNames { get; init; }
    public required string[] EnemyMonsterEntries { get; init; }
    public required HotRootPetSnapshot[] RootPets { get; init; }
    public required byte[] RootAllyOwnerSlots { get; init; }
    public required HotCoverageManifest CoverageManifest { get; init; }

    public required IReadOnlyDictionary<string, ushort> CardDefIdByEntry { get; init; }
    public required IReadOnlyDictionary<string, ushort> PowerDefIdByEntry { get; init; }
    public required IReadOnlyDictionary<string, ushort> RelicDefIdByEntry { get; init; }
    public required IReadOnlyDictionary<string, ushort> MonsterDefIdByEntry { get; init; }

    // Pre-resolved Phase 1 well-known DefIds (0 if not registered)
    public ushort VulnerablePowerDefId { get; init; }
    public ushort WeakPowerDefId { get; init; }
    public ushort FrailPowerDefId { get; init; }
    public ushort StrengthPowerDefId { get; init; }
    public ushort DexterityPowerDefId { get; init; }
    public ushort MetallicizePowerDefId { get; init; }
    public ushort PlatingPowerDefId { get; init; }
    public ushort BarricadePowerDefId { get; init; }
    public ushort ArtifactPowerDefId { get; init; }
    public ushort ThornsPowerDefId { get; init; }
    public ushort RitualPowerDefId { get; init; }
    public ushort BurningBloodRelicDefId { get; init; }
    public ushort OddMushroomRelicDefId { get; init; }
    public ushort PaperFrogRelicDefId { get; init; }

    public bool HasBurningBlood { get; init; }
    public bool HasOddMushroom { get; init; }
    public bool HasPaperFrog { get; init; }
    public bool HasPaperPhrog => HasPaperFrog || HasRelic(HotRelicId.PaperPhrog);
    public bool HasPaperKrane => HasRelic(HotRelicId.PaperKrane);

    public ReadOnlySpan<HotCardEffectOp> GetCardOps(ushort cardDefId)
    {
        HotCardDef def = Cards[cardDefId];
        return def.OpCount == 0
            ? ReadOnlySpan<HotCardEffectOp>.Empty
            : EffectOps.AsSpan(def.OpStart, def.OpCount);
    }

    public ReadOnlySpan<HotCardEffectOp> GetPotionOps(ushort potionDefId)
    {
        HotPotionDef def = Potions[potionDefId];
        return def.OpCount == 0
            ? ReadOnlySpan<HotCardEffectOp>.Empty
            : PotionEffectOps.AsSpan(def.OpStart, def.OpCount);
    }

    public ushort GetCardDefId(HotCustomCardId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)CardDefIdByCardId.Length ? CardDefIdByCardId[idx] : (ushort)0;
    }

    public ushort GetPowerDefId(HotPowerId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)PowerDefIdByPowerId.Length ? PowerDefIdByPowerId[idx] : (ushort)0;
    }

    public ushort GetRelicDefId(HotRelicId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)RelicDefIdByRelicId.Length ? RelicDefIdByRelicId[idx] : (ushort)0;
    }

    public bool HasRelic(HotRelicId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)HasActiveRelicByRelicId.Length && HasActiveRelicByRelicId[idx];
    }

    public byte GetRelicStateSlot(HotRelicId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)RelicStateSlotByRelicId.Length ? RelicStateSlotByRelicId[idx] : (byte)0xFF;
    }

    public ushort GetPotionDefId(HotCustomPotionId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)PotionDefIdByPotionId.Length ? PotionDefIdByPotionId[idx] : (ushort)0;
    }

    public ushort GetMonsterDefId(HotMonsterId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)MonsterDefIdByMonsterId.Length ? MonsterDefIdByMonsterId[idx] : (ushort)0;
    }

    public byte GetOrbDefId(HotOrbId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)OrbDefIdByOrbId.Length ? OrbDefIdByOrbId[idx] : (byte)0;
    }

    public byte GetEnchantmentDefId(HotEnchantmentId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)EnchDefIdByEnchId.Length ? EnchDefIdByEnchId[idx] : (byte)0;
    }

    public byte GetAfflictionDefId(HotAfflictionId id)
    {
        int idx = (int)id;
        return (uint)idx < (uint)AfflDefIdByAfflId.Length ? AfflDefIdByAfflId[idx] : (byte)0;
    }

    public HotMonsterMachine? GetMonsterMachine(ushort monsterDefId)
    {
        if (monsterDefId >= Monsters.Length)
            return null;
        int idx = Monsters[monsterDefId].MachineIndex;
        return idx >= 0 && idx < MonsterMachines.Length ? MonsterMachines[idx] : null;
    }

    public ushort FindCardDefId(string entry)
        => CardDefIdByEntry.TryGetValue(entry, out ushort id) ? id : (ushort)0;

    public ushort FindPowerDefId(string entry)
        => PowerDefIdByEntry.TryGetValue(entry, out ushort id) ? id : (ushort)0;

    public ushort FindRelicDefId(string entry)
        => RelicDefIdByEntry.TryGetValue(entry, out ushort id) ? id : (ushort)0;

    /// <summary>
    /// Resolves the exact STS2 0.114.0 <c>CardFactory.GetDefaultTransformationOptions</c>
    /// (<c>CardFactory.cs:171-176</c>) source pool for <paramref name="origDef"/>.
    /// </summary>
    public ushort[] GetUnlockedTransformPool(HotCardDef origDef)
    {
        // CardFactory.cs:173: Quest or Event/Ancient/Token -> ColorlessCardPool; else original.Pool.
        if (origDef.Type == CardType.Quest
            || origDef.Rarity is CardRarity.Event or CardRarity.Ancient or CardRarity.Token)
        {
            return CombatColorlessPool;
        }

        // Dispatch by card domain (= vanilla CardModel.Pool) rather than intercepting Status/Curse
        // rarity first: KnowledgeDemon Status cards (Amnesia, Disintegration, MindRot, Sloth, WasteAway)
        // have CardRarity.Status but belong to TokenCardPool (HotCardDomain.Token).
        HotCardDomain domain = origDef.Domain;
        return domain switch
        {
            HotCardDomain.Curse => UnlockedCurseCardPool,
            HotCardDomain.Status => UnlockedStatusCardPool,
            HotCardDomain.Token => UnlockedTokenCardPool,
            HotCardDomain.Colorless or HotCardDomain.Event or HotCardDomain.Quest => CombatColorlessPool,
            HotCardDomain.Tarot => TarotCardPool,
            HotCardDomain.Ironclad
                or HotCardDomain.Silent
                or HotCardDomain.Defect
                or HotCardDomain.Regent
                or HotCardDomain.Necrobinder
                or HotCardDomain.Fool => ResolveCharacterDomainTransformPool(domain),
            _ => origDef.Rarity switch
            {
                CardRarity.Curse => UnlockedCurseCardPool,
                CardRarity.Status => UnlockedStatusCardPool,
                _ => UnlockedCharacterCardPool,
            },
        };
    }

    private ushort[] ResolveCharacterDomainTransformPool(HotCardDomain domain)
    {
        int idx = (int)domain;
        if ((uint)idx < (uint)UnlockedCharacterPoolsByDomain.Length
            && UnlockedCharacterPoolsByDomain[idx] is { Length: > 0 } domainPool)
        {
            return domainPool;
        }
        return UnlockedCharacterCardPool;
    }

    /// <summary>
    /// Exact zero-allocation card comparator matching <see cref="CombatSolver.Engine.InCombat.Simulation.CombatCardOrdering"/>
    /// for <c>HotPiles.ShuffleDiscardIntoDraw</c>.
    /// </summary>
    public int CompareCardsForShuffle(
        in HotCard a,
        in HotCard b,
        in CombatHotState s,
        HotColdSidecar? cold)
    {
        HotCardDef defA = Cards[a.CardDefId];
        HotCardDef defB = Cards[b.CardDefId];

        int cmp = string.CompareOrdinal(defA.Entry, defB.Entry);
        if (cmp != 0)
            return cmp;

        cmp = a.UpgradeLevel.CompareTo(b.UpgradeLevel);
        if (cmp != 0)
            return cmp;

        if (a.EnchId != b.EnchId)
        {
            if (a.EnchId == 0)
                return -1;
            if (b.EnchId == 0)
                return 1;
            cmp = string.CompareOrdinal(Enchantments[a.EnchId].Entry, Enchantments[b.EnchId].Entry);
            if (cmp != 0)
                return cmp;
        }

        if (a.AfflId != b.AfflId)
        {
            if (a.AfflId == 0)
                return -1;
            if (b.AfflId == 0)
                return 1;
            cmp = string.CompareOrdinal(Afflictions[a.AfflId].Entry, Afflictions[b.AfflId].Entry);
            if (cmp != 0)
                return cmp;
        }

        int costA = (a.Flags & HotCardFlags.CostsX) != 0
            ? a.CapturedEnergy
            : Math.Max(0, GetCardLocalEnergyCost(in a, cold));
        int costB = (b.Flags & HotCardFlags.CostsX) != 0
            ? b.CapturedEnergy
            : Math.Max(0, GetCardLocalEnergyCost(in b, cold));
        cmp = costA.CompareTo(costB);
        if (cmp != 0)
            return cmp;

        cmp = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (cmp != 0)
            return cmp;

        string[] namesA = defA.OrderedVarNames;
        if (namesA.Length == 0)
            return 0;

        string[] namesB = defB.OrderedVarNames;
        if (ReferenceEquals(namesA, namesB) || namesA.Length == namesB.Length)
        {
            for (int i = 0; i < namesA.Length; i++)
            {
                if (!ReferenceEquals(namesA, namesB) && !string.Equals(namesA[i], namesB[i], StringComparison.Ordinal))
                    break;
                decimal valA = GetCardVarValue(in a, defA, i, cold);
                decimal valB = GetCardVarValue(in b, defB, i, cold);
                cmp = valA.CompareTo(valB);
                if (cmp != 0)
                    return cmp;
            }
            if (ReferenceEquals(namesA, namesB))
                return 0;
        }

        for (int i = 0; i < namesA.Length; i++)
        {
            int idxB = FindOrderedNameIndex(namesB, namesA[i]);
            if (idxB < 0)
                continue;
            decimal valA = GetCardVarValue(in a, defA, i, cold);
            decimal valB = GetCardVarValue(in b, defB, idxB, cold);
            cmp = valA.CompareTo(valB);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    public static decimal GetCardVarValue(in HotCard card, HotCardDef def, int varIndex, HotColdSidecar? cold = null)
    {
        decimal value = card.UpgradeLevel > 0 && varIndex < def.UpgradedVarValues.Length
            ? def.UpgradedVarValues[varIndex]
            : def.BaseVarValues[varIndex];
        if (varIndex == def.Var0Index)
        {
            if (card.Var0Delta != 0)
                value += card.Var0Delta;
            if (cold != null && card.CardUid != 0)
                value += HotColdSidecar.GetDecimalVarDelta(cold, card.CardUid);
        }
        else if (varIndex == def.Var1Index && card.Var1Delta != 0)
        {
            value += card.Var1Delta;
        }
        return value;
    }

    /// <summary>
    /// Computes <c>CardEnergyCost.GetWithModifiers(CostModifiers.Local)</c> matching STS2 0.114.0.
    /// </summary>
    public static int GetCardLocalEnergyCost(in HotCard card, HotColdSidecar? cold)
        => HotCostModifiers.GetLocalEnergyCost(in card, cold);

    /// <summary>
    /// Computes <c>CardModel.CurrentStarCost</c> matching STS2 0.114.0.
    /// </summary>
    public static int GetCardCurrentStarCost(in HotCard card, HotColdSidecar? cold)
        => HotCostModifiers.GetEffectiveStarCost(in card, cold);

    private static int FindOrderedNameIndex(string[] sortedNames, string target)
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

    // ---- Phase 1 Vertical Slice Bytecode & Coverage Catalog ----

    public static bool IsPhase1SupportedCard(string entry)
        => entry is
            "STRIKE_IRONCLAD" or
            "DEFEND_IRONCLAD" or
            "BASH" or
            "ANGER" or
            "CLEAVE" or
            "POMMEL_STRIKE" or
            "SHRUG_IT_OFF" or
            "TWIN_STRIKE" or
            "IRON_WAVE" or
            "CLOTHESLINE" or
            "INFLAME" or
            "METALLICIZE" or
            "ASCENDERS_BANE" or
            // STS2 0.113.1 Act 1 Ironclad equivalents
            "THUNDERCLAP" or
            "STONE_ARMOR" or
            "BARRICADE" or
            "BLUDGEON" or
            "IMPERVIOUS" or
            "TREMBLE";

    public static bool IsPhase1SupportedPower(string entry)
        => entry is
            "VULNERABLE_POWER" or
            "WEAK_POWER" or
            "FRAIL_POWER" or
            "STRENGTH_POWER" or
            "DEXTERITY_POWER" or
            "METALLICIZE_POWER" or
            "PLATING_POWER" or
            "BARRICADE_POWER" or
            "ARTIFACT_POWER" or
            "THORNS_POWER" or
            "RITUAL_POWER";

    public static bool IsPhase1SupportedRelic(string entry)
        => entry is
            "BURNING_BLOOD" or
            "ODD_MUSHROOM" or
            "PAPER_FROG";

    public static bool IsPhase1SupportedMonsterType(string typeName)
        => typeName is
            "FuzzyWurmCrawler" or
            "Nibbit" or
            "BowlbugEgg" or
            "BowlbugRock" or
            "CalcifiedCultist" or
            "SludgeSpinner" or
            "Flyconid" or
            "CorpseSlug";

    public static HotHookMask GetPowerHookMask(string entry)
        => HotPowerCatalog.GetHookMask(entry);

    public static HotHookMask GetRelicHookMask(string entry)
        => HotRelicCatalog.GetHookMask(entry);

    public static void CompilePhase1CardBytecode(
        string entry,
        ushort cardDefId,
        Func<string, ushort> ensurePowerDefId,
        List<HotCardEffectOp> ops,
        out int opStart,
        out byte opCount,
        out ushort customHandlerId)
    {
        HotCustomCardId cardId = HotCardCatalog.FindByEntry(entry);
        HotCardCatalog.CompileCardBytecode(
            cardId,
            cardDefId,
            powerId => ensurePowerDefId(HotPowerCatalog.Get(powerId).Entry),
            ops,
            out opStart,
            out opCount,
            out HotCustomCardId customId);
        customHandlerId = (ushort)customId;
    }
}
