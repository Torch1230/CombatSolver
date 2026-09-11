using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record CardChoiceSpec(
    PlanChoiceEffect Effect,
    PileType SourcePile,
    int MinCount,
    int MaxCount,
    IReadOnlyList<PredictedCard> Options,
    IReadOnlyList<PredictedCard> SourceCards,
    double ReplacementValue,
    string ContextId = "",
    int? MaxBranches = null);

internal static partial class CardChoiceSupport
{
    // Reserve only a very small number of physical representatives when the result can depend on
    // which equal-looking card was selected. We prefer replacing a duplicate semantic selection,
    // but may use this bounded overflow rather than erase the only copy of a different decision.
    // An equivalence class with many copies therefore still cannot multiply the whole frontier.
    internal const int MaximumIdentityOccurrenceReservedBranches = 2;
    /// <summary>
    /// 会自己离场的牌在移除排序里加的偏置。取值只要大过任何一张牌的估值即可，作用是让它排到
    /// 最后，而不是与普通牌按数值竞争。
    /// </summary>
    private const double SelfClearingRemovalPenalty = 1_000d;

    /// <summary>
    /// 原版起手打击、防御在移除排序里改用的权重。
    /// </summary>
    /// <remarks>
    /// 通用估值把伤害记满、格挡打八折，于是打击 <c>6.0</c> 高于防御 <c>4.0</c>，任何角色都先移除
    /// 防御。原版五个角色的实战优先级相反：起手防御比起手打击更该留，先移除的应该是打击。
    ///
    /// 用权重而不是写死数值，是为了保住升级差别：起手打击 <c>6</c> 伤害得 <c>4.0</c>、升级后
    /// <c>9</c> 伤害得 <c>6.0</c>；起手防御 <c>5</c> 格挡得 <c>6.0</c>、升级后 <c>8</c> 格挡得
    /// <c>9.6</c>。升级过的那张仍然更靠后移除。
    ///
    /// 只覆盖原版这十张具体的起手牌。其他来源的打击、防御——包括 mod 角色的——继续走通用估值，
    /// 因为它们的强弱取决于各自的机制，这里没有依据替它们排序。Mod 作者自己有这个依据，
    /// 可以用 <see cref="CardRemovalValueMirrors"/> 给一个相对通用估值的偏置。
    /// </remarks>
    private const double BasicStrikeRemovalWeight = 2d / 3d;

    /// <inheritdoc cref="BasicStrikeRemovalWeight" />
    private const double BasicDefendRemovalWeight = 1.2d;

    private static readonly HashSet<string> UnsupportedExistingChoiceCards =
    [
        "Tutor"
    ];

    public static bool RequiresUnsupportedExistingChoice(CardModel card)
        => UnsupportedExistingChoiceCards.Contains(card.GetType().Name);

    public static PlanCardChoice? BuildRequiredEmptyChoice(CardModel card)
    {
        return card switch
        {
            HiddenDaggers => new PlanCardChoice(PlanChoiceEffect.Discard, PileType.Hand, []),
            Brand or Scavenge => new PlanCardChoice(PlanChoiceEffect.Exhaust, PileType.Hand, []),
            _ => null,
        };
    }

    public static CardChoiceSpec? GetSpec(CombatPredictionSimulator simulator, PredictedCard playedCard)
    {
        // 第三方登记优先。登记表为空时这里只是一次计数比较，原版一条也走不进来。
        if (CardChoiceMirrors.TryGetSpec(simulator, playedCard, out CardChoiceSpec registered))
            return registered;

        SimPlayerCombatState owner = simulator.State.GetPlayerCombatState(playedCard.Preview.Owner);
        CardModel card = playedCard.Preview;
        IEnumerable<PredictedCard> discardBeforeResolution = owner.DiscardPile.Cards
            .Where(item => !ReferenceEquals(item.Original, playedCard.Original));

        CombatPredictionCardGenerationOptionsEntry? generated = simulator.History
            .OfType<CombatPredictionCardGenerationOptionsEntry>()
            .LastOrDefault(entry => playedCard.References(entry.Trace?.Source));
        if (generated != null)
        {
            int minCount = card is Abundance ? 1 : 0;
            return RangeSpec(
                owner,
                PlanChoiceEffect.GenerateToHand,
                PileType.None,
                minCount,
                1,
                generated.Options);
        }

        return card switch
        {
            SeekerStrike => BuildSeekerSpec(simulator, playedCard, owner),
            TrueGrit when card.IsUpgraded => Spec(owner, PlanChoiceEffect.Exhaust, PileType.Hand, 1, owner.Hand.Cards),
            Hologram => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Discard, 1, discardBeforeResolution),
            // Native FromCombatPile exits before requesting a selection when the hit
            // ended combat. A phantom choice cannot be deployed or reconciled later.
            Graveblast when !simulator.IsEnding => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Discard, 1, discardBeforeResolution),
            Headbutt => Spec(owner, PlanChoiceEffect.MoveToDrawTop, PileType.Discard, 1, discardBeforeResolution),
            CosmicIndifference => Spec(owner, PlanChoiceEffect.MoveToDrawTop, PileType.Discard, 1, discardBeforeResolution),
            SecretWeapon => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Draw, 1,
                owner.DrawPile.Cards.Where(item => item.Preview.Type == CardType.Attack)),
            SecretTechnique => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Draw, 1,
                owner.DrawPile.Cards.Where(item => item.Preview.Type == CardType.Skill)),
            Wish => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Draw, 1, owner.DrawPile.Cards),
            Dredge => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Discard,
                Math.Min(card.DynamicVars.Cards.IntValue,
                    simulator.GetMaxHandSize(card.Owner) - owner.Hand.Cards.Count),
                discardBeforeResolution),
            NeowsFury => RangeSpec(owner, PlanChoiceEffect.MoveToHand, PileType.Discard,
                0,
                Math.Min(card.DynamicVars.Cards.IntValue,
                    simulator.GetMaxHandSize(card.Owner) - owner.Hand.Cards.Count),
                discardBeforeResolution),
            Survivor or Acrobatics or DaggerThrow => Spec(owner, PlanChoiceEffect.Discard, PileType.Hand, 1, owner.Hand.Cards),
            BurningPact => Spec(owner, PlanChoiceEffect.Exhaust, PileType.Hand, 1, owner.Hand.Cards),
            Prepared => Spec(owner, PlanChoiceEffect.Discard, PileType.Hand, card.DynamicVars.Cards.IntValue, owner.Hand.Cards),
            ThinkingAhead => Spec(owner, PlanChoiceEffect.MoveToDrawTop, PileType.Hand, 1, owner.Hand.Cards),
            Glimmer or PhotonCut => Spec(owner, PlanChoiceEffect.MoveToDrawTop, PileType.Hand,
                card.DynamicVars["PutBack"].IntValue, owner.Hand.Cards),
            Scavenge => Spec(owner, PlanChoiceEffect.Exhaust, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => !ReferenceEquals(item, playedCard))),
            Armaments when !card.IsUpgraded => Spec(owner, PlanChoiceEffect.Upgrade, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => item.Preview.IsUpgradable)),
            Begone => Spec(owner, PlanChoiceEffect.Transform, PileType.Hand, 1, owner.Hand.Cards),
            Charge => Spec(owner, PlanChoiceEffect.Transform, PileType.Draw,
                card.DynamicVars.Cards.IntValue, owner.DrawPile.Cards),
            Guards => RangeSpec(owner, PlanChoiceEffect.Transform, PileType.Hand,
                0, owner.Hand.Cards.Count, owner.Hand.Cards,
                (card.IsUpgraded ? 10d : 7d) * 0.8d),
            DualWield => Spec(owner, PlanChoiceEffect.Duplicate, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => item.Preview.Type is CardType.Attack or CardType.Power)),
            HiddenDaggers => Spec(owner, PlanChoiceEffect.Discard, PileType.Hand,
                card.DynamicVars.Cards.IntValue, owner.Hand.Cards),
            Purity => RangeSpec(owner, PlanChoiceEffect.Exhaust, PileType.Hand,
                0, card.DynamicVars.Cards.IntValue, owner.Hand.Cards),
            Seance => Spec(owner, PlanChoiceEffect.Transform, PileType.Draw,
                card.DynamicVars.Cards.IntValue, owner.DrawPile.Cards),
            Transfigure => Spec(owner, PlanChoiceEffect.Modify, PileType.Hand, 1, owner.Hand.Cards),
            Brand => Spec(owner, PlanChoiceEffect.Exhaust, PileType.Hand, 1, owner.Hand.Cards),
            Cleanse => Spec(owner, PlanChoiceEffect.Exhaust, PileType.Draw, 1, owner.DrawPile.Cards),
            Nightmare => Spec(owner, PlanChoiceEffect.Nightmare, PileType.Hand, 1, owner.Hand.Cards),
            HandTrick => Spec(owner, PlanChoiceEffect.ApplySly, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => item.Preview.Type == CardType.Skill && !item.Preview.IsSlyThisTurn)),
            HeirloomHammer => Spec(owner, PlanChoiceEffect.Duplicate, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => item.Preview.VisualCardPool.IsColorless)),
            SculptingStrike => Spec(owner, PlanChoiceEffect.ApplyEthereal, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => !item.Preview.GetKeywordsWithSources(KeywordSources.Local)
                    .Contains(CardKeyword.Ethereal))),
            Snap => Spec(owner, PlanChoiceEffect.ApplyRetain, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => !item.Preview.Keywords.Contains(CardKeyword.Retain))),
            DecisionsDecisions => Spec(owner, PlanChoiceEffect.AutoPlayRepeated, PileType.Hand, 1,
                owner.Hand.Cards.Where(item => item.Preview.Type == CardType.Skill
                    && !item.Preview.Keywords.Contains(CardKeyword.Unplayable))),
            _ => null,
        };
    }

    public static PlanCardChoice BuildAutomaticPolicyChoice(CardChoiceSpec spec)
    {
        int count = Math.Min(spec.MinCount, spec.Options.Count);
        bool fromHand = spec.SourcePile == PileType.Hand;
        List<PredictedCard> selection = (fromHand
                ? spec.Options.OrderBy(card => CardValue(card.Preview))
                : spec.Options.OrderByDescending(card => CardValue(card.Preview)))
            .ThenBy(ChoiceCardKey, StringComparer.Ordinal)
            .Take(count)
            .ToList();
        return new PlanCardChoice(
            spec.Effect,
            spec.SourcePile,
            ToTokens(selection, spec.Options, spec.SourceCards, static card => card.Id.Entry),
            ContextId: spec.ContextId);
    }

    public static PlanCardChoice BuildVakuuChoice(CardChoiceSpec spec)
    {
        int count = Math.Min(spec.MaxCount, spec.Options.Count);
        IReadOnlyList<PredictedCard> selected = spec.Options.Take(count).ToArray();
        return new PlanCardChoice(
            spec.Effect,
            spec.SourcePile,
            ToTokens(selected, spec.Options, spec.SourceCards, static card => card.Id.Entry),
            ContextId: spec.ContextId);
    }

    public static bool RequiresAutomaticNestedChoice(
        CombatPredictionSimulator simulator,
        CardChoiceSpec outerSpec,
        PlanCardChoice outerChoice)
    {
        if (outerSpec.Effect != PlanChoiceEffect.AutoPlayRepeated || outerChoice.Cards.Count == 0)
            return false;
        PlanCardToken token = outerChoice.Cards[0];
        PredictedCard selected = outerSpec.Options
            .Where(card => MatchesToken(card, token))
            .Skip(token.OptionOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"嵌套选牌检查找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}。");
        return GetSpec(simulator, selected) != null
            || BuildRequiredEmptyChoice(selected.Preview) != null
            || selected.Preview is Abundance or Discovery or Quasar or Splash;
    }

    public static IReadOnlyList<PlanCardChoice> BuildChoices(
        CardChoiceSpec spec,
        SolverDisplayNames displayNames,
        int maxPileBranches,
        int maxHandBranches)
    {
        if (spec.MaxCount < spec.MinCount)
            return [];

        int minTake = Math.Min(spec.MinCount, spec.Options.Count);
        int maxTake = Math.Min(spec.MaxCount, spec.Options.Count);
        int branchLimit = spec.SourcePile == PileType.Hand
            ? maxHandBranches
            : maxPileBranches;
        bool exactSingleCardRouting = spec.MaxCount == 1
            && spec.Effect is PlanChoiceEffect.MoveToHand
                or PlanChoiceEffect.MoveToDrawTop
                or PlanChoiceEffect.MoveToHandFreeThisTurn
                or PlanChoiceEffect.SetFreeThisCombat
                or PlanChoiceEffect.GenerateToHand;
        if (exactSingleCardRouting)
        {
            int skipBranch = minTake == 0 ? 1 : 0;
            branchLimit = Math.Max(branchLimit, spec.Options.Count + skipBranch);
        }
        bool diversifyHandDiscard = spec.SourcePile == PileType.Hand
            && spec.Effect is PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw
            && minTake == maxTake
            && maxTake > 1;
        List<PredictedCard> ordered = (spec.Effect is PlanChoiceEffect.Discard
                or PlanChoiceEffect.DiscardAndDraw
                or PlanChoiceEffect.Exhaust
                or PlanChoiceEffect.Transform
                ? spec.Options.OrderBy(card => RemovalPriority(spec, card))
                : spec.Options.OrderByDescending(card => CardValue(card.Preview)))
            .ThenBy(ChoiceCardKey, StringComparer.Ordinal)
            .ToList();
        string[] orderedSemanticKeys = ordered
            .Select(ChoiceCardKey)
            .ToArray();
        List<IReadOnlyList<PredictedCard>> selections = [];
        List<IReadOnlyList<PredictedCard>> cardinalityRepresentatives = [];
        for (int take = minTake; take <= maxTake; take++)
        {
            List<IReadOnlyList<PredictedCard>> sameSize = [];
            int combinationLimit = diversifyHandDiscard
                ? Math.Max(branchLimit, Math.Min(256, checked(branchLimit * 8)))
                : branchLimit;
            BuildCombinations(
                ordered,
                orderedSemanticKeys,
                take,
                0,
                [],
                sameSize,
                combinationLimit);
            if (sameSize.Count > 0)
                cardinalityRepresentatives.Add(sameSize[0]);
            selections.AddRange(sameSize);
        }

        int effectiveBranchLimit = Math.Max(branchLimit, cardinalityRepresentatives.Count);
        List<IReadOnlyList<PredictedCard>> retained = diversifyHandDiscard
            ? BuildHandDiscardRepresentatives(spec, selections, effectiveBranchLimit)
            : cardinalityRepresentatives.ToList();
        if (!diversifyHandDiscard)
        {
            retained.AddRange(selections
                .OrderByDescending(selection => ChoicePriority(spec, selection))
                .Where(selection => !retained.Contains(selection))
                .Take(effectiveBranchLimit - retained.Count));
        }

        if (IsIdentityChangingPersistentChoiceEffect(spec.Effect))
        {
            ReserveIdentityOccurrenceRepresentatives(
                spec,
                retained,
                effectiveBranchLimit,
                MaximumIdentityOccurrenceReservedBranches);
        }

        IEnumerable<IReadOnlyList<PredictedCard>> orderedRetained = retained
            .OrderByDescending(selection => ChoicePriority(spec, selection));
        if (IsIdentityChangingPersistentChoiceEffect(spec.Effect))
            orderedRetained = OrderSemanticSelectionsBeforeOccurrenceSupplements(orderedRetained);

        return orderedRetained
            .Take(spec.MaxBranches ?? int.MaxValue)
            .Select(selection => new PlanCardChoice(
                spec.Effect,
                spec.SourcePile,
                ToTokens(selection, spec.Options, spec.SourceCards, displayNames.Card),
                ContextId: spec.ContextId))
            .ToList();
    }

    internal static bool IsIdentityChangingPersistentChoiceEffect(PlanChoiceEffect effect)
        => effect is PlanChoiceEffect.Exhaust
            or PlanChoiceEffect.Upgrade
            or PlanChoiceEffect.Transform
            or PlanChoiceEffect.Duplicate
            or PlanChoiceEffect.Modify
            or PlanChoiceEffect.Nightmare
            or PlanChoiceEffect.SetFreeThisCombat
            or PlanChoiceEffect.ApplySly
            or PlanChoiceEffect.ApplyEthereal
            or PlanChoiceEffect.ApplyRetain;

    internal static int AddIdentityOccurrenceBranchReserve(
        PlanChoiceEffect effect,
        int branchLimit)
    {
        if (branchLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(branchLimit));
        if (branchLimit == int.MaxValue
            || !IsIdentityChangingPersistentChoiceEffect(effect))
        {
            return branchLimit;
        }
        return branchLimit > int.MaxValue - MaximumIdentityOccurrenceReservedBranches
            ? int.MaxValue
            : branchLimit + MaximumIdentityOccurrenceReservedBranches;
    }

    /// <summary>
    /// Applies a semantic layer limit without allowing a physical-occurrence supplement to take
    /// the place of a distinct decision. Supplements whose canonical choice survived may use the
    /// same bounded +2 reserve as <see cref="BuildChoices"/>.
    /// </summary>
    internal static IReadOnlyList<PlanCardChoice> TakeChoicesWithIdentityOccurrenceReserve(
        IReadOnlyList<PlanCardChoice> choices,
        PlanChoiceEffect effect,
        int semanticLimit,
        int occurrenceReserveLimit = MaximumIdentityOccurrenceReservedBranches)
    {
        if (semanticLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(semanticLimit));
        if (occurrenceReserveLimit < 0
            || occurrenceReserveLimit > MaximumIdentityOccurrenceReservedBranches)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrenceReserveLimit));
        }
        if (choices.Count <= semanticLimit)
            return choices;
        if (!IsIdentityChangingPersistentChoiceEffect(effect))
            return choices.Take(semanticLimit).ToList();

        List<PlanCardChoice> retained = [];
        foreach (PlanCardChoice choice in choices)
        {
            if (retained.Count >= semanticLimit)
                break;
            if (!retained.Any(existing => SameSemanticChoice(existing, choice)))
                retained.Add(choice);
        }
        int maximumRetained = semanticLimit > int.MaxValue - occurrenceReserveLimit
            ? int.MaxValue
            : semanticLimit + occurrenceReserveLimit;
        foreach (PlanCardChoice candidate in choices)
        {
            if (retained.Count >= maximumRetained)
                break;
            if (!retained.Contains(candidate)
                && retained.Any(existing => SameSemanticChoice(existing, candidate)))
            {
                retained.Add(candidate);
            }
        }
        return retained;
    }

    internal static int CountSemanticChoices(IReadOnlyList<PlanCardChoice> choices)
    {
        List<PlanCardChoice> representatives = [];
        foreach (PlanCardChoice choice in choices)
        {
            if (!representatives.Any(existing => SameSemanticChoice(existing, choice)))
                representatives.Add(choice);
        }
        return representatives.Count;
    }

    private static IReadOnlyList<IReadOnlyList<PredictedCard>>
        OrderSemanticSelectionsBeforeOccurrenceSupplements(
            IEnumerable<IReadOnlyList<PredictedCard>> selections)
    {
        List<IReadOnlyList<PredictedCard>> semanticRepresentatives = [];
        List<IReadOnlyList<PredictedCard>> occurrenceSupplements = [];
        foreach (IReadOnlyList<PredictedCard> selection in selections)
        {
            if (semanticRepresentatives.Any(candidate =>
                    SameSemanticSelection(candidate, selection)))
            {
                occurrenceSupplements.Add(selection);
            }
            else
            {
                semanticRepresentatives.Add(selection);
            }
        }
        semanticRepresentatives.AddRange(occurrenceSupplements);
        return semanticRepresentatives;
    }

    internal static bool SameSemanticChoice(PlanCardChoice left, PlanCardChoice right)
    {
        if (left.Effect != right.Effect
            || left.SourcePile != right.SourcePile
            || !string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
            || !string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
            || left.Timing != right.Timing
            || left.Cards.Count != right.Cards.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Cards.Count; index++)
        {
            PlanCardToken leftCard = left.Cards[index];
            PlanCardToken rightCard = right.Cards[index];
            if (!string.Equals(leftCard.CardId, rightCard.CardId, StringComparison.Ordinal)
                || leftCard.UpgradeLevel != rightCard.UpgradeLevel
                || !string.Equals(leftCard.StateKey, rightCard.StateKey, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct IdentityOccurrenceSupplement(
        IReadOnlyList<PredictedCard> Canonical,
        IReadOnlyList<PredictedCard> Representative);

    private static void ReserveIdentityOccurrenceRepresentatives(
        CardChoiceSpec spec,
        List<IReadOnlyList<PredictedCard>> retained,
        int branchLimit,
        int reservedLimit)
    {
        if (reservedLimit <= 0 || branchLimit <= 0)
            return;

        IReadOnlyList<IdentityOccurrenceSupplement> supplements =
            BuildIdentityOccurrenceSupplements(spec, retained, reservedLimit);
        int maximumRetained = branchLimit > int.MaxValue - reservedLimit
            ? int.MaxValue
            : branchLimit + reservedLimit;
        List<IReadOnlyList<PredictedCard>> admittedCanonicals = [];
        List<IReadOnlyList<PredictedCard>> admittedRepresentatives = [];
        foreach (IdentityOccurrenceSupplement supplement in supplements)
        {
            if (!retained.Any(candidate =>
                    SamePhysicalSelection(candidate, supplement.Canonical)))
            {
                continue;
            }

            if (retained.Count >= branchLimit)
            {
                int evictionIndex = FindIdentityOccurrenceEvictionCandidate(
                    spec,
                    retained,
                    admittedCanonicals,
                    admittedRepresentatives,
                    supplement.Canonical);
                if (evictionIndex >= 0)
                {
                    retained.RemoveAt(evictionIndex);
                }
                else if (retained.Count >= maximumRetained)
                {
                    continue;
                }
            }

            int canonicalIndex = retained.FindIndex(candidate =>
                SamePhysicalSelection(candidate, supplement.Canonical));
            if (canonicalIndex < 0)
                continue;
            retained.Insert(canonicalIndex + 1, supplement.Representative);
            admittedCanonicals.Add(supplement.Canonical);
            admittedRepresentatives.Add(supplement.Representative);
        }
    }

    private static IReadOnlyList<IdentityOccurrenceSupplement> BuildIdentityOccurrenceSupplements(
        CardChoiceSpec spec,
        IReadOnlyList<IReadOnlyList<PredictedCard>> retained,
        int limit)
    {
        if (limit <= 0)
            return [];

        List<IdentityOccurrenceSupplement> supplements = [];
        foreach (IReadOnlyList<PredictedCard> selection in retained
                     .OrderByDescending(candidate => ChoicePriority(spec, candidate)))
        {
            IReadOnlyList<PredictedCard>? tailRepresentative =
                BuildTailOccurrenceRepresentative(selection, spec.Options);
            if (tailRepresentative == null
                || retained.Any(candidate => SamePhysicalSelection(candidate, tailRepresentative))
                || supplements.Any(candidate =>
                    SamePhysicalSelection(candidate.Representative, tailRepresentative)))
            {
                continue;
            }

            supplements.Add(new IdentityOccurrenceSupplement(selection, tailRepresentative));
            if (supplements.Count >= limit)
                break;
        }
        return supplements;
    }

    private static int FindIdentityOccurrenceEvictionCandidate(
        CardChoiceSpec spec,
        IReadOnlyList<IReadOnlyList<PredictedCard>> retained,
        IReadOnlyList<IReadOnlyList<PredictedCard>> admittedCanonicals,
        IReadOnlyList<IReadOnlyList<PredictedCard>> admittedRepresentatives,
        IReadOnlyList<PredictedCard> protectedCanonical)
    {
        int selectedIndex = -1;
        double selectedPriority = double.PositiveInfinity;
        for (int index = 0; index < retained.Count; index++)
        {
            IReadOnlyList<PredictedCard> candidate = retained[index];
            if (SamePhysicalSelection(candidate, protectedCanonical)
                || admittedCanonicals.Any(canonical =>
                    SamePhysicalSelection(candidate, canonical))
                || admittedRepresentatives.Any(representative =>
                    SamePhysicalSelection(candidate, representative))
                // A physical-occurrence supplement is not a substitute for a different
                // semantic choice. Only evict when that exact decision remains represented.
                || !HasSemanticSelectionSibling(retained, index)
                || retained.Count(other => other.Count == candidate.Count) <= 1)
            {
                continue;
            }

            double priority = ChoicePriority(spec, candidate);
            if (priority <= selectedPriority)
            {
                selectedIndex = index;
                selectedPriority = priority;
            }
        }
        return selectedIndex;
    }

    private static bool HasSemanticSelectionSibling(
        IReadOnlyList<IReadOnlyList<PredictedCard>> retained,
        int candidateIndex)
    {
        IReadOnlyList<PredictedCard> candidate = retained[candidateIndex];
        for (int index = 0; index < retained.Count; index++)
        {
            if (index != candidateIndex
                && SameSemanticSelection(candidate, retained[index]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SameSemanticSelection(
        IReadOnlyList<PredictedCard> left,
        IReadOnlyList<PredictedCard> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    ChoiceCardKey(left[index]),
                    ChoiceCardKey(right[index]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<PredictedCard>? BuildTailOccurrenceRepresentative(
        IReadOnlyList<PredictedCard> selection,
        IReadOnlyList<PredictedCard> options)
    {
        if (selection.Count == 0)
            return null;

        Dictionary<string, int> selectedCounts = new(StringComparer.Ordinal);
        foreach (PredictedCard card in selection)
        {
            string key = ChoiceCardKey(card);
            selectedCounts[key] = selectedCounts.GetValueOrDefault(key) + 1;
        }

        Dictionary<string, PredictedCard[]> tailByKey = new(StringComparer.Ordinal);
        bool hasDifferentRepresentative = false;
        foreach ((string key, int count) in selectedCounts)
        {
            PredictedCard[] equivalentOptions = options
                .Where(option => string.Equals(ChoiceCardKey(option), key, StringComparison.Ordinal))
                .ToArray();
            if (equivalentOptions.Length < count)
                return null;

            PredictedCard[] tail = equivalentOptions[^count..];
            tailByKey[key] = tail;
            PredictedCard[] selectedForKey = selection
                .Where(card => string.Equals(ChoiceCardKey(card), key, StringComparison.Ordinal))
                .ToArray();
            hasDifferentRepresentative |= !SamePhysicalSelection(selectedForKey, tail);
        }
        if (!hasDifferentRepresentative)
            return null;

        Dictionary<string, int> offsets = new(StringComparer.Ordinal);
        PredictedCard[] representative = new PredictedCard[selection.Count];
        for (int index = 0; index < selection.Count; index++)
        {
            string key = ChoiceCardKey(selection[index]);
            int offset = offsets.GetValueOrDefault(key);
            representative[index] = tailByKey[key][offset];
            offsets[key] = offset + 1;
        }
        return representative;
    }

    private static bool SamePhysicalSelection(
        IReadOnlyList<PredictedCard> left,
        IReadOnlyList<PredictedCard> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
                return false;
        }
        return true;
    }

    private static List<IReadOnlyList<PredictedCard>> BuildHandDiscardRepresentatives(
        CardChoiceSpec spec,
        IReadOnlyList<IReadOnlyList<PredictedCard>> selections,
        int limit)
    {
        List<IReadOnlyList<PredictedCard>> ranked = selections
            .OrderByDescending(selection => ChoicePriority(spec, selection))
            .ToList();
        List<IReadOnlyList<PredictedCard>> retained = [];

        void Add(IReadOnlyList<PredictedCard>? selection)
        {
            if (selection != null && retained.Count < limit && !retained.Contains(selection))
                retained.Add(selection);
        }

        Add(ranked.FirstOrDefault());
        Add(ranked
            .OrderByDescending(selection => selection.Count(card => card.Preview.IsSlyThisTurn))
            .ThenByDescending(selection => ChoicePriority(spec, selection))
            .FirstOrDefault());
        Add(ranked
            .OrderByDescending(selection => selection.Count(card =>
                card.Preview.Type is CardType.Status or CardType.Curse
                || card.Preview.GetKeywordsWithSources(KeywordSources.Local)
                    .Contains(CardKeyword.Unplayable)))
            .ThenByDescending(selection => ChoicePriority(spec, selection))
            .FirstOrDefault());
        Add(ranked
            .OrderByDescending(selection => selection.Count(card => card.Preview.ShouldRetainThisTurn))
            .ThenByDescending(selection => ChoicePriority(spec, selection))
            .FirstOrDefault());
        Add(ranked
            .OrderByDescending(selection => selection.Sum(card => CardValue(card.Preview)))
            .ThenByDescending(selection => ChoicePriority(spec, selection))
            .FirstOrDefault());

        foreach (PredictedCard option in spec.Options.DistinctBy(ChoiceCardKey))
        {
            string optionKey = ChoiceCardKey(option);
            Add(ranked.FirstOrDefault(selection => selection.Any(card => ChoiceCardKey(card) == optionKey)));
        }
        foreach (IReadOnlyList<PredictedCard> selection in ranked)
            Add(selection);
        return retained;
    }

    public static PlanCardChoice BuildRequestedChoice(
        CardChoiceSpec spec,
        IReadOnlyList<string> cardIds)
    {
        List<PredictedCard> remaining = spec.Options.ToList();
        List<PredictedCard> selected = [];
        if (cardIds.Count == 1 && cardIds[0] == "__FIRST__" && remaining.Count > 0)
        {
            selected.Add(remaining[0]);
            remaining.RemoveAt(0);
        }
        else
        {
            foreach (string cardId in cardIds)
            {
                PredictedCard card = remaining.FirstOrDefault(candidate =>
                        candidate.Preview.Id.Entry.Equals(cardId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"测试选牌候选中找不到 {cardId}。");
                selected.Add(card);
                remaining.Remove(card);
            }
        }

        int effectiveMin = Math.Min(spec.MinCount, spec.Options.Count);
        int effectiveMax = Math.Min(spec.MaxCount, spec.Options.Count);
        if (selected.Count < effectiveMin || selected.Count > effectiveMax)
        {
            throw new InvalidOperationException(
                $"测试计划选择 {selected.Count} 张牌，但模拟选择要求 {effectiveMin}..{effectiveMax} 张。");
        }
        return new PlanCardChoice(
            spec.Effect,
            spec.SourcePile,
            ToTokens(selected, spec.Options, spec.SourceCards, static card => card.Id.Entry),
            ContextId: spec.ContextId);
    }

    public static IReadOnlyList<PredictedCard> ResolveStandaloneChoice(
        CombatPredictionSimulator simulator,
        PlanCardChoice choice,
        IReadOnlyList<PredictedCard> options,
        int expectedCount,
        PileType sourcePile)
    {
        SimCardPile source = simulator.State.GetPlayerCombatState(options[0].Preview.Owner).GetCardPile(sourcePile)
            ?? throw new InvalidOperationException($"回合开始选牌找不到牌堆 {sourcePile}。");
        List<PredictedCard> selected = [];
        foreach (PlanCardToken token in choice.Cards)
        {
            PredictedCard card = options.Where(candidate => MatchesToken(candidate, token))
                .Skip(token.OptionOccurrence)
                .FirstOrDefault()
                ?? throw new InvalidPlannedChoiceBranchException(
                    $"回合开始选牌时找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}。");
            if (!source.Cards.Contains(card))
            {
                throw new InvalidPlannedChoiceBranchException(
                    $"回合开始选中的 {token.CardId} 已不在 {sourcePile} 中。");
            }
            selected.Add(card);
        }
        if (selected.Count != expectedCount)
        {
            throw new InvalidPlannedChoiceBranchException(
                $"回合开始计划选择 {selected.Count} 张牌，但当前要求 {expectedCount} 张。");
        }
        return selected;
    }

    private static CardChoiceSpec? Spec(
        SimPlayerCombatState owner,
        PlanChoiceEffect effect,
        PileType source,
        int count,
        IEnumerable<PredictedCard> options,
        double replacementValue = 0d)
    {
        List<PredictedCard> list = options.ToList();
        IReadOnlyList<PredictedCard> sourceCards = owner.GetCardPile(source)?.Cards ?? [];
        return list.Count == 0
            ? null
            : new CardChoiceSpec(effect, source, count, count, list, sourceCards, replacementValue);
    }

    private static CardChoiceSpec RangeSpec(
        SimPlayerCombatState owner,
        PlanChoiceEffect effect,
        PileType source,
        int minCount,
        int maxCount,
        IEnumerable<PredictedCard> options,
        double replacementValue = 0d)
    {
        List<PredictedCard> list = options.ToList();
        IReadOnlyList<PredictedCard> sourceCards = owner.GetCardPile(source)?.Cards ?? [];
        return new CardChoiceSpec(effect, source, minCount, maxCount, list, sourceCards, replacementValue);
    }

    private static CardChoiceSpec? BuildSeekerSpec(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        SimPlayerCombatState owner)
    {
        List<PredictedCard> options = owner.DrawPile.Cards
            .ToList()
            .StableShuffle(simulator.Rng.CombatCardSelection)
            .Take(playedCard.Preview.DynamicVars.Cards.IntValue)
            .ToList();
        if (options.Count == 0)
            return null;
        simulator.History.CardsSelected(options);
        simulator.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
        string contextId = $"seeker:{simulator.Rng.CombatCardSelection.Counter()}:" +
            string.Join(',', options.Select(card =>
                $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}"));
        return new CardChoiceSpec(
            PlanChoiceEffect.MoveToHand,
            PileType.Draw,
            1,
            1,
            options,
            owner.DrawPile.Cards,
            ReplacementValue: 0d,
            contextId);
    }

    private static void BuildCombinations(
        IReadOnlyList<PredictedCard> options,
        IReadOnlyList<string> semanticKeys,
        int count,
        int start,
        List<PredictedCard> current,
        List<IReadOnlyList<PredictedCard>> output,
        int limit)
    {
        if (output.Count >= limit)
            return;
        if (current.Count == count)
        {
            output.Add(current.ToList());
            return;
        }
        for (int i = start; i <= options.Count - (count - current.Count); i++)
        {
            string optionKey = semanticKeys[i];
            bool alreadyVisitedAtDepth = false;
            for (int prior = start; prior < i; prior++)
            {
                if (!string.Equals(semanticKeys[prior], optionKey, StringComparison.Ordinal))
                    continue;
                alreadyVisitedAtDepth = true;
                break;
            }
            if (alreadyVisitedAtDepth)
                continue;
            current.Add(options[i]);
            BuildCombinations(
                options,
                semanticKeys,
                count,
                i + 1,
                current,
                output,
                limit);
            current.RemoveAt(current.Count - 1);
            if (output.Count >= limit)
                return;
        }
    }

    private static IReadOnlyList<PlanCardToken> ToTokens(
        IReadOnlyList<PredictedCard> selected,
        IReadOnlyList<PredictedCard> options,
        IReadOnlyList<PredictedCard> source,
        Func<CardModel, string> displayName)
    {
        List<PlanCardToken> tokens = [];
        foreach (PredictedCard card in selected)
        {
            string stateKey = ChoiceCardKey(card);
            int sourceOccurrence = source.TakeWhile(item => !ReferenceEquals(item, card))
                .Count(item => HasStableTokenIdentity(item, card));
            int optionOccurrence = options.TakeWhile(item => !ReferenceEquals(item, card))
                .Count(item => HasStableTokenIdentity(item, card));
            tokens.Add(new PlanCardToken(
                card.Preview.Id.Entry,
                card.Preview.CurrentUpgradeLevel,
                stateKey,
                sourceOccurrence,
                optionOccurrence,
                displayName(card.Preview)));
        }
        return tokens;
    }

    private static PredictedCard Find(IReadOnlyList<PredictedCard> cards, PlanCardToken token)
    {
        return cards.Where(card => MatchesToken(card, token))
            .Skip(token.SourceOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidPlannedChoiceBranchException(
                $"选牌回放时找不到 {token.CardId}+{token.UpgradeLevel}#{token.SourceOccurrence}；" +
                $"候选={string.Join(',', cards.Select(ChoiceCardKey))}。");
    }

    /// <summary>移除排序键，只给无人测试用。</summary>
    internal static double RemovalPriorityForTesting(CardChoiceSpec spec, PredictedCard card)
        => RemovalPriority(spec, card);

    private static double ChoicePriority(CardChoiceSpec spec, IReadOnlyList<PredictedCard> cards)
    {
        double value = cards.Sum(card => spec.Effect is PlanChoiceEffect.Transform or PlanChoiceEffect.Exhaust
            ? RemovalPriority(spec, card)
            : CardValue(card.Preview));
        if (spec.Effect == PlanChoiceEffect.Discard && spec.SourcePile != PileType.Hand)
            return ReorderDiscardPriority(spec, cards);
        return spec.Effect switch
        {
            PlanChoiceEffect.Transform => cards.Count * spec.ReplacementValue - value,
            PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw =>
                cards.Sum(DiscardTriggerValue) - value,
            PlanChoiceEffect.Exhaust => -value,
            _ => value,
        };
    }

    /// <summary>
    /// 从手牌以外的牌堆弃牌时的分支排序键。
    /// </summary>
    /// <remarks>
    /// 从手牌弃掉一张牌，这张牌本回合就用不上了，所以按"损失"计价是对的。从抽牌堆弃掉一张
    /// 不一样：牌没有离开本场牌库，只是被推到循环的后面，换来的是下一次抽牌更靠前地拿到别的牌。
    /// 那是一次重排，不是一次损失。预视就是这个形状。
    ///
    /// 所以收益按"换掉它能好多少"算：弃掉一张牌，下次抽到的是这一堆里剩下的牌，期望值取源牌堆
    /// 的平均。低于平均的牌弃掉是正收益，高于平均的是负收益，一张都不弃恰好是零。不需要新的
    /// 调参常数。
    ///
    /// 沿用原有口径的两点：仍然把 Sly 的弃牌触发收益加上；源牌堆为空时平均按零算。
    /// </remarks>
    private static double ReorderDiscardPriority(
        CardChoiceSpec spec,
        IReadOnlyList<PredictedCard> cards)
    {
        double replacement = spec.SourceCards.Count == 0
            ? 0d
            : spec.SourceCards.Average(card => CardValue(card.Preview));
        return cards.Sum(card => replacement - CardValue(card.Preview) + DiscardTriggerValue(card));
    }

    private static double DiscardTriggerValue(PredictedCard card)
    {
        if (!card.Preview.IsSlyThisTurn)
            return 0d;
        return CardValue(card.Preview) * 2d
            + DynamicVarBaseValue(card.Preview.DynamicVars, "Energy") * 12d
            + DynamicVarBaseValue(card.Preview.DynamicVars, "Stars") * 12d;
    }

    /// <summary>
    /// 移除类选择的排序键，从低到高优先移除。
    /// </summary>
    /// <remarks>
    /// 会自己离场的牌不值得占用一次移除。虚无牌在回合结束时若仍在手牌，自己就会消耗掉
    /// （见 <c>CombatPredictionSimulator.EndTurn</c> 的虚无分支），所以把一次消耗花在它身上，
    /// 换来的只是本回合剩下的一个手牌位；花在打击、防御这类牌上，换来的是整场战斗之后每一次
    /// 抽牌的质量。两者不是一个量级。
    ///
    /// 判据限定在手牌来源：虚无只在手牌里触发，抽牌堆或弃牌堆里的同一张牌本回合不会自己走，
    /// 那时移除它是真正的牌库压缩。转变分支原来不分牌堆，现在一并按同一条判据限定，
    /// 否则从抽牌堆转变时会把一张本回合不会离场的牌当成会离场的。
    ///
    /// 弃牌不适用：弃掉的牌回到弃牌堆、仍在本场牌库里，没有压缩可言，而把打不出的牌从手上
    /// 弃掉本来就是弃牌该干的事。所以这里只覆盖消耗与转变。
    /// </remarks>
    private static double RemovalPriority(CardChoiceSpec spec, PredictedCard card)
    {
        double value = spec.Effect is PlanChoiceEffect.Exhaust or PlanChoiceEffect.Transform
            ? BasicCardRemovalValue(card.Preview) ?? CardValue(card.Preview)
            : CardValue(card.Preview);
        if (LeavesOnItsOwn(spec, card))
            value += SelfClearingRemovalPenalty;
        return value;
    }

    /// <summary>原版起手打击、防御的移除估值；其他牌返回 <c>null</c> 走通用估值。</summary>
    /// <remarks>
    /// 不限来源牌堆。哪张牌更该留是牌本身的性质，从手牌消耗和从抽牌堆转变应当得到同一个排序。
    /// 只有虚无那一条才限定手牌，因为它依赖"回合结束时在不在手上"。
    /// </remarks>
    private static double? BasicCardRemovalValue(CardModel card)
        => card switch
        {
            StrikeIronclad or StrikeSilent or StrikeDefect or StrikeNecrobinder or StrikeRegent
                => DynamicVarBaseValue(card.DynamicVars, "Damage") * BasicStrikeRemovalWeight,
            DefendIronclad or DefendSilent or DefendDefect or DefendNecrobinder or DefendRegent
                => DynamicVarBaseValue(card.DynamicVars, "Block") * BasicDefendRemovalWeight,
            _ => ThirdPartyBasicCardRemovalValue(card),
        };

    /// <summary>
    /// 第三方登记过的牌：通用估值加上它给的偏置；没登记过的照旧返回 <c>null</c> 走通用估值。
    /// </summary>
    /// <remarks>
    /// 上面那张表只列原版十张，理由是求解器没有依据替 Mod 的牌排序。那个判断对求解器成立，
    /// 对 Mod 作者不成立——他知道自己那张牌在自己这套体系里值多少。所以由他给一个偏置，
    /// 见 <see cref="CardRemovalValueMirrors"/>。偏置取到负值时这张牌会让「烧它」这条分支排在
    /// 「一张都不选」之前，也就是从「少亏一点」变成「值得烧」。登记表为空时这里立刻返回
    /// <c>null</c>。
    /// </remarks>
    private static double? ThirdPartyBasicCardRemovalValue(CardModel card)
        => CardRemovalValueMirrors.Offset(card) is { } offset
            ? CardValue(card) + offset
            : null;

    /// <summary>这张牌会不会不花移除资源就自己离场。</summary>
    /// <remarks>
    /// 关键字只读本地来源，与转变分支原有的判据一致：涵盖规范关键字和音乐盒这种直接写在牌上的
    /// 来源，不涵盖诅咒之触那种由其他 Model 持续授予的全局来源。要覆盖全局来源需要把战斗状态
    /// 一路传进选牌构建，那是另一件事。
    /// </remarks>
    private static bool LeavesOnItsOwn(CardChoiceSpec spec, PredictedCard card)
    {
        if (!card.Preview.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal))
            return false;
        return spec.Effect is PlanChoiceEffect.Exhaust or PlanChoiceEffect.Transform
            && spec.SourcePile == PileType.Hand;
    }

    internal static double CardValue(CardModel card)
    {
        double damage = DynamicVarBaseValue(card.DynamicVars, "Damage");
        double block = DynamicVarBaseValue(card.DynamicVars, "Block");
        double draw = DynamicVarBaseValue(card.DynamicVars, "Cards");
        double power = card.Type == CardType.Power ? 8d : 0d;
        return damage + block * 0.8d + draw * 3d + power;
    }

    internal static double DynamicVarBaseValue(DynamicVarSet dynamicVars, string key)
        => dynamicVars.TryGetValue(key, out DynamicVar? dynamicVar)
            ? (double)dynamicVar.BaseValue
            : 0d;

    internal static string ChoiceCardKey(CardModel card)
        => ChoiceCardKey(card, discoverUnregisteredBaseLibModifiers: true);

    private static string ChoiceCardKey(
        CardModel card,
        bool discoverUnregisteredBaseLibModifiers)
    {
        string vars = string.Join(';', card.DynamicVars
            .OrderBy(item => item.Key)
            .Where(item => SemanticStateFieldPolicy.IsSemantic(card, item.Key, item.Value))
            .Select(item => $"{item.Key}={item.Value.BaseValue}"));
        string keywords = string.Join(',', card.GetKeywordsWithSources(KeywordSources.Local).Order());
        StringBuilder key = new();
        key.Append(card.Id.Entry).Append('+').Append(card.CurrentUpgradeLevel)
            .Append("|energy=").Append(card.EnergyCost.CostsX).Append(':')
            .Append(card.EnergyCost.GetWithModifiers(CostModifiers.Local))
            .Append("|stars=").Append(card.HasStarCostX).Append(':').Append(card.CurrentStarCost)
            .Append("|replay=").Append(card.BaseReplayCount)
            .Append("|exhaust=").Append(card.ExhaustOnNextPlay)
            .Append("|sly=").Append(card.IsSlyThisTurn)
            .Append("|retain=").Append(card.ShouldRetainThisTurn)
            .Append("|deck=").Append(card.DeckVersion != null)
            .Append("|keywords=").Append(keywords)
            .Append("|vars=").Append(vars).Append('|')
            .Append(card.Enchantment == null ? "-" : EnchantmentStateSupport.Describe(card.Enchantment))
            .Append('|').Append(card.Affliction?.Id.Entry).Append(':').Append(card.Affliction?.Amount ?? 0)
            .Append("|baselib=");
        if (!PredictionModModelSupport.AppendBaseLibCardModifierState(
                key,
                card,
                discoverUnregisteredBaseLibModifiers))
            key.Append('-');
        return key.ToString();
    }

    internal static string ChoiceCardKey(PredictedCard card)
    {
        if (card.TryGetCachedChoiceKey(out string key))
            return key;
        key = ChoiceCardKey(card.Preview, discoverUnregisteredBaseLibModifiers: false);
        card.SetCachedChoiceKey(key);
        return key;
    }

    internal static bool MatchesToken(CardModel card, PlanCardToken token)
        => card.Id.Entry == token.CardId
            && card.CurrentUpgradeLevel == token.UpgradeLevel;

    internal static bool MatchesToken(PredictedCard card, PlanCardToken token)
        => card.Preview.Id.Entry == token.CardId
            && card.Preview.CurrentUpgradeLevel == token.UpgradeLevel;

    private static bool HasStableTokenIdentity(PredictedCard left, PredictedCard right)
        => left.Preview.Id.Entry == right.Preview.Id.Entry
            && left.Preview.CurrentUpgradeLevel == right.Preview.CurrentUpgradeLevel;
}
