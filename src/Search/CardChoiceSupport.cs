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
    string ContextId = "");

internal static partial class CardChoiceSupport
{
    // Reserve only a very small number of physical representatives when the result can depend on
    // which equal-looking card was selected. We prefer replacing a duplicate semantic selection,
    // but may use this bounded overflow rather than erase the only copy of a different decision.
    // An equivalence class with many copies therefore still cannot multiply the whole frontier.
    internal const int MaximumIdentityOccurrenceReservedBranches = 2;

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
            Graveblast => Spec(owner, PlanChoiceEffect.MoveToHand, PileType.Discard, 1, discardBeforeResolution),
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
                ? spec.Options.OrderBy(card => RemovalPriority(spec.Effect, card))
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

    private static double ChoicePriority(CardChoiceSpec spec, IReadOnlyList<PredictedCard> cards)
    {
        double value = cards.Sum(card => spec.Effect == PlanChoiceEffect.Transform
            ? RemovalPriority(spec.Effect, card)
            : CardValue(card.Preview));
        return spec.Effect switch
        {
            PlanChoiceEffect.Transform => cards.Count * spec.ReplacementValue - value,
            PlanChoiceEffect.Discard or PlanChoiceEffect.DiscardAndDraw =>
                cards.Sum(DiscardTriggerValue) - value,
            PlanChoiceEffect.Exhaust => -value,
            _ => value,
        };
    }

    private static double DiscardTriggerValue(PredictedCard card)
    {
        if (!card.Preview.IsSlyThisTurn)
            return 0d;
        return CardValue(card.Preview) * 2d
            + DynamicVarBaseValue(card.Preview.DynamicVars, "Energy") * 12d
            + DynamicVarBaseValue(card.Preview.DynamicVars, "Stars") * 12d;
    }

    private static double RemovalPriority(PlanChoiceEffect effect, PredictedCard card)
    {
        double value = CardValue(card.Preview);
        if (effect == PlanChoiceEffect.Transform
            && card.Preview.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal))
        {
            value += 1_000d;
        }
        return value;
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
