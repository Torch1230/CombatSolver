using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.HotKernel;

/// <summary>
/// Standalone HotKernel import helpers (RNG capture, semantic DynamicVar filter,
/// compiled monster field reader, static monster values, theft/gold rules, combat pool filter,
/// and live choice-card key formatting) with zero dependencies on legacy Engine/Prediction/Search.
/// </summary>
internal static class HotImportSupport
{
    public static readonly CardTag[] MelodyTags = [CardTag.Wah, CardTag.Cho, CardTag.Ki];

    private static readonly FieldInfo MomentumExtraDamageField =
        typeof(Momentum).GetField("_extraDamage", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Momentum).FullName, "_extraDamage");

    private static readonly HashSet<(Type ModelType, string FieldName)> PresentationOnlyVarFields =
    [
        (typeof(KnockdownPower), "Applier"),
        (typeof(ImpaledPower), "Applier"),
        (typeof(GuardedPower), "Applier"),
        (typeof(InterceptPower), "Covering"),
        (typeof(VitalSparkPower), "AfflictionTitle"),
        (typeof(GalvanicPower), "AfflictionTitle"),
        (typeof(NightmarePower), "Card"),
        (typeof(FlankingPower), "Applier"),
        (typeof(ShrinkPower), "ApplierName"),
        (typeof(ImitationLearningPower), "TargetPlayer"),
        (typeof(CoveredPower), "Applier"),
        (typeof(BarricadePower), "ApplierName"),
    ];

    private static readonly IReadOnlyDictionary<string, string[]> StaticMonsterIntMembers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LivingFog"] = ["BloatAmount"],
            ["ToughEgg"] = ["HatchlingMinHp", "HatchlingMaxHp"],
            ["TestSubject"] = ["BurningGrowlBurnCount", "BurningGrowlStrengthGain"],
            ["BowlbugEgg"] = ["ProtectBlock"],
            ["InfestedPrism"] = ["VitalSparkAmount"],
            ["KinFollower"] = ["DanceStrength"],
            ["KinPriest"] = ["RitualStrength"],
            ["KnowledgeDemon"] = ["PonderStrength"],
            ["LagavulinMatriarch"] = ["Slash2Block", "DebuffAmount"],
            ["Myte"] = ["SuckStrength"],
            ["Nibbit"] = ["SliceBlock", "HissStrengthGain"],
            ["MagiKnight"] = ["PowerShieldBlock"],
            ["Seapunk"] = ["BubbleBlock", "BubbleStr"],
            ["BowlbugNectar"] = ["BuffStrengthGain"],
            ["CorpseSlug"] = ["GoopFrailAmt"],
            ["SoulFysh"] = ["ScreamMoveAmount", "GazeMoveAmount"],
            ["TheLost"] = ["DebilitatingSmogStrengthStealAmount"],
            ["CalcifiedCultist"] = ["IncantationAmount"],
            ["DampCultist"] = ["IncantationAmount"],
            ["CeremonialBeast"] = ["PlowStrength", "CrushStrength", "PlowAmount", "RitualAmount"],
            ["Ovicopter"] = ["NutritionalPasteStrengthAmount"],
            ["SkulkingColony"] = ["InertiaStrengthGain"],
            ["TheInsatiable"] = ["SalivateStrength", "SandpitAmount"],
            ["Vantom"] = ["PrepareStrength"],
            ["TheObscura"] = ["HardeningStrikeBlock"],
            ["TheForgotten"] = ["DebilitatingSmogDexStealAmount"],
            ["LouseProgenitor"] = ["CurlBlock", "GrowStrength"],
            ["Tunneler"] = ["BlockGain"],
            ["Crusher"] = ["AdaptStrengthGain"],
            ["Rocket"] = ["ChargeUpStrengthGain"],
            ["PhantasmalGardener"] = ["EnlargeStr"],
            ["Axebot"] = ["BootUpBlock", "BootUpStrGain", "RespawnCount"],
            ["Aeonglass"] = ["EbbBlock", "WitherAmount", "IncreasingIntensityBaseStrength"],
            ["AxeRubyRaider"] = ["SwingBlock"],
            ["WaterfallGiant"] = ["PressurizeAmount", "SiphonHeal", "PressureGunIncrease", "SteamBoostAmount"],
        };

    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, int>> IntAccessors = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, bool>> BoolAccessors = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, object?>> ObjectAccessors = new();

    public static HotRngState CaptureRng(Rng rng) => new()
    {
        S0 = rng._random._s0,
        S1 = rng._random._s1,
        S2 = rng._random._s2,
        S3 = rng._random._s3,
        Counter = rng._counter,
        Pad = 0,
    };

    public static bool IsSemanticVar(AbstractModel model, string fieldName, DynamicVar value)
    {
        if (value is CalculatedVar)
            return false;
        if (value is not StringVar)
            return true;
        if (PresentationOnlyVarFields.Contains((model.GetType(), fieldName)))
            return false;
        // Defensive fallback: vanilla 0.113.1 StringVars are presentation/title text only;
        // never crash root import on an unexpected StringVar.
        return false;
    }

    public static int SemanticallyRelevantAmountOnTurnStart(PowerModel power)
        => power is DrawCardsNextTurnPower or SummonNextTurnPower or HelloWorldPower or GodByrdPower
            ? power.AmountOnTurnStart
            : 0;

    public static bool UsesNativeDurationSkip(Type type)
        => type == typeof(WeakPower) || type == typeof(VulnerablePower) || type == typeof(FrailPower);

    public static bool SemanticallyRelevantSkipNextDurationTick(PowerModel power)
        => UsesNativeDurationSkip(power.GetType()) && power.SkipNextDurationTick;

    public static int ArrowSlotIndex(IReadOnlyList<ArrowSlot> arrows, ArrowSlot arrow)
    {
        for (int i = 0; i < arrows.Count; i++)
        {
            if (ReferenceEquals(arrows[i], arrow))
                return i;
        }
        throw new InvalidOperationException("Impaled arrow has no owning player slot.");
    }

    public static bool ForfeitsEncounterGold(CombatState combat)
        => combat.RunState.GameMode != GameMode.Blitz
            && combat.Encounter is GremlinMercNormal
            && combat.EscapedCreatures.Any(static c => c.Monster is FatGremlin);

    public static bool IsTheftEncounter(CombatState state)
        => state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK"
            || state.Creatures.Any(static c => c.Monster is GremlinMerc or ThievingHopper or FatGremlin);

    public static IEnumerable<CardModel> FilterForCombatAndPlayerCount(
        IEnumerable<CardModel> cards,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        IEnumerable<CardModel> combatCards = CardFactory.FilterForCombat(cards);
        return multiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly
            ? combatCards.Where(static c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            : combatCards.Where(static c => c.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly);
    }

    public static int CaptureAttackBaseDamage(AttackIntent attack, MonsterModel monster, string moveId)
    {
        Func<decimal> calculate = attack.DamageCalc
            ?? throw PredictionUnsupportedException.ForContent(
                $"Attack intent has no prediction damage calculation: {monster.GetType().FullName}.{moveId}/{attack.GetType().FullName}.",
                attack.GetType(), monster.GetType());
        // PreRegisterSpawnableMonsters compiles unattached encounter prototypes (e.g. dead TheForgotten
        // during mid-combat CaptureLive); TheForgotten.DreadDamage reads base.Creature.GetPowerAmount<DexterityPower>(),
        // which throws when _creature == null. Use the 0.113.1 TheForgotten.cs:28 base value (0 Dexterity).
        if (monster._creature == null
            && monster is TheForgotten
            && string.Equals(moveId, "DREAD", StringComparison.Ordinal))
        {
            return AscensionHelper.GetValueIfAscension(AscensionLevel.DeadlyEnemies, 15, 13);
        }
        return Math.Max(0, (int)calculate());
    }

    public static IReadOnlyDictionary<string, int> CaptureStaticMonsterIntValues(MonsterModel monster)
    {
        if (!StaticMonsterIntMembers.TryGetValue(monster.GetType().Name, out string[]? members))
            return new Dictionary<string, int>(0, StringComparer.Ordinal);
        Dictionary<string, int> values = new(members.Length, StringComparer.Ordinal);
        for (int i = 0; i < members.Length; i++)
            values.Add(members[i], ReadMonsterInt(monster, members[i]));
        return values;
    }

    public static int ReadMonsterInt(MonsterModel monster, string name)
        => IntAccessors.GetOrAdd((monster.GetType(), name), static key => BuildIntAccessor(key.Type, key.Name))(monster);

    public static bool ReadMonsterBool(MonsterModel monster, string name)
        => BoolAccessors.GetOrAdd((monster.GetType(), name), static key => BuildBoolAccessor(key.Type, key.Name))(monster);

    public static object? ReadMonsterObject(MonsterModel monster, string name)
        => ObjectAccessors.GetOrAdd((monster.GetType(), name), static key => BuildObjectAccessor(key.Type, key.Name))(monster);

    /// <summary>
    /// Formats a live vanilla <see cref="CardModel"/> into the exact choice-card key grammar
    /// matching <see cref="HotContinuationStamp.FormatChoiceCardKey"/> (used for root dupe sources).
    /// </summary>
    public static string FormatLiveChoiceCardKey(CardModel card, CardModel? dupeSource = null)
    {
        string vars = string.Join(';', card.DynamicVars
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Where(item => IsSemanticVar(card, item.Key, item.Value))
            .Select(static item => $"{item.Key}={item.Value.BaseValue}"));
        string keywords = string.Join(',', card.GetKeywordsWithSources(KeywordSources.Local).Order());

        StringBuilder key = new(192);
        key.Append(card.Id.Entry).Append('+').Append(card.CurrentUpgradeLevel)
            .Append("|energy=").Append(card.EnergyCost.CostsX).Append(':')
            .Append(card.EnergyCost.GetWithModifiers(CostModifiers.Local))
            .Append("|stars=").Append(card.HasStarCostX).Append(':').Append(card.CurrentStarCost)
            .Append("|replay=").Append(card.BaseReplayCount).Append(':').Append(card.ExtraReplayThisTurn)
            .Append("|exhaust=").Append(card.ExhaustOnNextPlay)
            .Append("|sly=").Append(card.IsSlyThisTurn)
            .Append("|retain=").Append(card._hasSingleTurnRetain)
            .Append("|deck=").Append(card.DeckVersion != null)
            .Append("|keywords=").Append(keywords)
            .Append("|vars=").Append(vars).Append('|');

        if (card.Enchantment is { } enchantment)
        {
            key.Append(enchantment.Id.Entry)
                .Append(':').Append(enchantment.Amount)
                .Append(':').Append((int)enchantment.Status);
            if (enchantment is Momentum momentum)
                key.Append(':').Append((int)MomentumExtraDamageField.GetValue(momentum)!);
        }
        else
        {
            key.Append('-');
        }

        key.Append('|').Append(card.Affliction?.Id.Entry).Append(':').Append(card.Affliction?.Amount ?? 0)
            .Append("|baselib=-");

        List<LocalCostModifier> energy = card.EnergyCost._localModifiers;
        List<TemporaryCardCost> stars = card._temporaryStarCosts;
        if (energy.Count > 0 || stars.Count > 0)
        {
            key.Append("|cost-state=").Append(card.EnergyCost.GetWithModifiers(CostModifiers.None))
                .Append(':').Append(energy.Count).Append('[');
            for (int i = 0; i < energy.Count; i++)
            {
                LocalCostModifier m = energy[i];
                key.Append(m.Amount).Append(':').Append((int)m.Type)
                    .Append(':').Append((int)m.Expiration).Append(':').Append(m.IsReduceOnly).Append(',');
            }
            key.Append("]/stars=").Append(card.BaseStarCost).Append(':').Append(stars.Count).Append('[');
            for (int i = 0; i < stars.Count; i++)
            {
                TemporaryCardCost s = stars[i];
                key.Append(s.Cost).Append(':').Append(s.ClearsWhenTurnEnds)
                    .Append(':').Append(s.ClearsWhenCardIsPlayed).Append(',');
            }
            key.Append(']');
        }

        key.Append("|keywordsAdded=").AppendJoin(',', card._keywordsAddedFromOutside.Order());
        key.Append("|keywordsRemoved=").AppendJoin(',', card._keywordsRemovedFromOutside.Order());
        if (card is TheShot shot)
            key.Append("|florescence=").Append(shot._cardsExhaustedByFlorescence);
        if (card is ShowOff showOff)
            key.Append("|showOff=").Append(showOff.CurrentAim).Append(':').Append(showOff.IncreasedAim);
        key.Append("|dupe=").Append(card.IsDupe);
        if (card.IsDupe)
        {
            CardModel src = dupeSource ?? card.DupeOf
                ?? throw new InvalidOperationException("Dupe has no source card.");
            key.Append("|dupeSource={").Append(FormatLiveChoiceCardKey(src)).Append('}');
        }

        return key.ToString();
    }

    private static MemberExpression ResolveMember(Type type, string name, UnaryExpression typed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty(name, flags) is PropertyInfo prop
            ? Expression.Property(typed, prop)
            : type.GetField(name, flags) is FieldInfo field
                ? Expression.Field(typed, field)
                : throw new MissingMemberException(type.FullName, name);
    }

    private static Func<MonsterModel, int> BuildIntAccessor(Type type, string name)
    {
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        MemberExpression member = ResolveMember(type, name, Expression.Convert(input, type));
        Expression value = member.Type == typeof(int) ? member : Expression.Convert(member, typeof(int));
        return Expression.Lambda<Func<MonsterModel, int>>(value, input).Compile();
    }

    private static Func<MonsterModel, bool> BuildBoolAccessor(Type type, string name)
    {
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        MemberExpression member = ResolveMember(type, name, Expression.Convert(input, type));
        return Expression.Lambda<Func<MonsterModel, bool>>(member, input).Compile();
    }

    private static Func<MonsterModel, object?> BuildObjectAccessor(Type type, string name)
    {
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        MemberExpression member = ResolveMember(type, name, Expression.Convert(input, type));
        return Expression.Lambda<Func<MonsterModel, object?>>(Expression.Convert(member, typeof(object)), input).Compile();
    }
}
