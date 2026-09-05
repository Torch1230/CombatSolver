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
    /// <summary>
    /// 会自己离场的牌在移除排序里加的偏置。取值只要大过任何一张牌的估值即可，作用是让它排到
    /// 最后，而不是与普通牌按数值竞争。
    /// </summary>
    private const double SelfClearingRemovalPenalty = 1_000d;

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
                ? spec.Options.OrderBy(card => RemovalPriority(spec, card))
                : spec.Options.OrderByDescending(card => CardValue(card.Preview)))
            .ThenBy(ChoiceCardKey, StringComparer.Ordinal)
            .ToList();
        List<IReadOnlyList<PredictedCard>> selections = [];
        List<IReadOnlyList<PredictedCard>> cardinalityRepresentatives = [];
        for (int take = minTake; take <= maxTake; take++)
        {
            List<IReadOnlyList<PredictedCard>> sameSize = [];
            int combinationLimit = diversifyHandDiscard
                ? Math.Max(branchLimit, Math.Min(256, checked(branchLimit * 8)))
                : branchLimit;
            BuildCombinations(ordered, take, 0, [], sameSize, combinationLimit);
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

        return retained
            .OrderByDescending(selection => ChoicePriority(spec, selection))
            .Select(selection => new PlanCardChoice(
                spec.Effect,
                spec.SourcePile,
                ToTokens(selection, spec.Options, spec.SourceCards, displayNames.Card),
                ContextId: spec.ContextId))
            .ToList();
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
        string? previousKey = null;
        for (int i = start; i <= options.Count - (count - current.Count); i++)
        {
            string optionKey = ChoiceCardKey(options[i]);
            if (optionKey == previousKey)
                continue;
            previousKey = optionKey;
            current.Add(options[i]);
            BuildCombinations(options, count, i + 1, current, output, limit);
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
        double value = cards.Sum(card => spec.Effect is PlanChoiceEffect.Transform or PlanChoiceEffect.Exhaust
            ? RemovalPriority(spec, card)
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
    /// 那时移除它是真正的牌库压缩。
    ///
    /// 弃牌不适用：弃掉的牌回到弃牌堆、仍在本场牌库里，没有压缩可言，而把打不出的牌从手上
    /// 弃掉本来就是弃牌该干的事。所以这里只覆盖消耗与转变。
    /// </remarks>
    private static double RemovalPriority(CardChoiceSpec spec, PredictedCard card)
    {
        double value = CardValue(card.Preview);
        if (LeavesOnItsOwn(spec, card))
            value += SelfClearingRemovalPenalty;
        return value;
    }

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
        return spec.Effect switch
        {
            PlanChoiceEffect.Transform => true,
            PlanChoiceEffect.Exhaust => spec.SourcePile == PileType.Hand,
            _ => false,
        };
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
