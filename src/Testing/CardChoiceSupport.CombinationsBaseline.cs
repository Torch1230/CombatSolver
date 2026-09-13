using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// Frozen pre-optimization enumeration; unchanged scoring/identity helpers are shared.
internal static partial class CardChoiceSupport
{
    internal static IReadOnlyList<PlanCardChoice> BuildChoicesBaselineForTesting(
        CardChoiceSpec spec,
        SolverDisplayNames displayNames,
        int maxPileBranches,
        int maxHandBranches)
    {
        if (spec.MaxCount < spec.MinCount)
            return [];
        if (spec.IsImplicitAllSelection)
            return [new PlanCardChoice(spec.Effect, spec.SourcePile,
                ToTokens(spec.Options, spec.Options, spec.SourceCards, displayNames.Card), ContextId: spec.ContextId)];

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
            BuildCombinationsBaselineForTesting(
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

    private static void BuildCombinationsBaselineForTesting(
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
            BuildCombinationsBaselineForTesting(
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

}
