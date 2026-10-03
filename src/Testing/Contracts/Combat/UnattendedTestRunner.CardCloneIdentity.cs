using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// B016/T024：永久的跑局牌组身份（<see cref="CardModel.DeckVersion"/>）只属于「牌组原牌在战斗里的克隆」。
    /// 预测侧任何把既有牌当成生成牌或玩法复制处理的路径都会让跨回合续接戳丢掉这份身份，
    /// 实机边界比较随即将该牌报成单字段差异（SEARCH_REUSE_MISS）。
    /// 本夹具把身份合同钉在同一张实机原牌上，按真实牌堆迁移逐段核对：
    /// 根捕获、写时复制、洗牌回抽牌堆、回手、玩法复制、以及同名牌转换替换。
    /// </summary>
    private async Task AssertCardCloneIdentityContractAsync(CombatState combat, Player player)
    {
        await Task.Yield();
        PlayerCombatState live = player.PlayerCombatState
            ?? throw new InvalidOperationException("卡牌克隆身份测试要求玩家战斗状态。");

        CardPile[] piles = [live.Hand, live.DrawPile, live.DiscardPile, live.ExhaustPile];
        CardModel[] allCards = [.. piles.SelectMany(pile => pile.Cards)];
        // 现场的单字段差异来自「同 ID 的永久牌组原牌 + 战斗生成副本」这一对实例，
        // 所以优先挑同时存在这两种实例的牌；没有副本时退回任意带身份的牌。
        CardModel deckCard = allCards
            .Where(card => card.DeckVersion != null)
            .OrderByDescending(card => allCards.Count(
                other => other.Id.Entry == card.Id.Entry && other.DeckVersion == null))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "卡牌克隆身份测试要求牌堆里至少有一张带永久牌组版本的实机原牌。");

        CardModel deckVersion = deckCard.DeckVersion
            ?? throw new InvalidOperationException("卡牌克隆身份测试找不到永久牌组版本。");
        string cardId = deckCard.Id.Entry;

        var simulator = new CombatPredictionSimulator(new SimulatedCombatState(combat));
        SimPlayerCombatState simState = simulator.State.GetPlayerCombatState(player);
        PredictedCard wrapper = simState.FindCard(deckCard)
            ?? throw new InvalidOperationException($"预测根找不到实机原牌 {cardId}，既有牌被当成新牌处理。");

        RequireDeckVersion(wrapper, deckVersion, cardId, "根捕获");

        if (wrapper.Preview.HasBeenRemovedFromState)
            throw new InvalidOperationException($"{cardId} 的预测根包装错误地带上了移出战斗标记。");

        wrapper.MaterializePreview();
        RequireDeckVersion(wrapper, deckVersion, cardId, "材质化预览");

        _ = wrapper.MutablePreview;
        RequireDeckVersion(wrapper, deckVersion, cardId, "写时复制");

        simulator.Shuffle(player);
        PredictedCard shuffled = simState.FindCard(deckCard)
            ?? throw new InvalidOperationException($"洗牌回抽牌堆后预测根丢失了实机原牌 {cardId}。");
        if (!ReferenceEquals(shuffled, wrapper))
            throw new InvalidOperationException($"洗牌为 {cardId} 重建了预测包装，既有牌被当成新牌处理。");
        RequireDeckVersion(shuffled, deckVersion, cardId, "洗牌回抽牌堆");

        simulator.AddToPile(wrapper, PileType.Hand);
        RequireDeckVersion(wrapper, deckVersion, cardId, "回手");

        // 跨回合循环：手牌弃空、抽牌堆空则把弃牌堆洗回抽牌堆、再抽牌。
        List<PredictedCard> handCards = [.. simState.Hand.Cards];
        if (handCards.Count > 0)
            simulator.Discard(handCards);
        RequireDeckVersion(wrapper, deckVersion, cardId, "弃牌堆");
        simulator.Draw(player, 5);
        RequireDeckVersion(wrapper, deckVersion, cardId, "洗牌回抽牌堆后抽牌");

        // 玩法复制是唯一按原版清空身份的分支；它也必须真的清空，并记下实机原牌。
        PredictedCard gameplayCopy = wrapper.CreateClone();
        if (gameplayCopy.Preview.DeckVersion != null)
            throw new InvalidOperationException($"{cardId} 的玩法复制错误地保留了永久牌组身份。");
        if (!ReferenceEquals(gameplayCopy.Preview.CloneOf, deckCard))
            throw new InvalidOperationException($"{cardId} 的玩法复制没有记下它的实机原牌。");

        // 同名牌转换替换：替换牌按原版是全新生成牌，实机原牌对象本身必须原封不动。
        CardModel canonical = ModelDb.AllCards.First(card => card.Id.Entry == cardId);
        CardModel replacement = PredictionUtils.CreateCard(canonical, player);
        CardChoiceSupport.TransformCardToGeneratedReplacement(simulator, wrapper, replacement);
        if (!ReferenceEquals(deckCard.DeckVersion, deckVersion))
            throw new InvalidOperationException($"预测侧转换改写了实机原牌 {cardId} 的永久牌组关联。");
        if (simState.FindCard(deckCard) != null)
            throw new InvalidOperationException($"{cardId} 转换后预测根仍能找到已被替换的原牌包装。");

        // 同名牌对 + 随机转换（PlanChoiceEffect.Transform）。原版合同（sts2.decompiled.cs）：
        //   CardCmd.Transform 193658-193706：战斗内目标只做 RemoveFromCurrentPile → AddInternal(replacement)，
        //     整段没有任何 DeckVersion 搬运；只有 PileType.Deck 分支才做 FloorAddedToDeck/CardsTransformed 记账。
        //   CardTransformation.GetReplacement 182557-182576 与 CardFactory.CreateRandomCardForTransform
        //     173509-173513 用 original.CardScope.CreateCard(...) 新建替换牌；
        //   CardModel.AfterCloned 74049-74070 把 DeckVersion 清空；
        //   GetFilteredTransformationOptions 173537 排除原牌自身 Id，所以转换永远不会产出同名牌。
        // 推论：战斗内转换的替换牌必须是无身份牌，且该路径不可能把现场那张原牌换成同名牌。
        var transformSimulator = new CombatPredictionSimulator(new SimulatedCombatState(combat));
        SimPlayerCombatState transformState = transformSimulator.State.GetPlayerCombatState(player);
        PredictedCard transformTarget = transformState.FindCard(deckCard)
            ?? throw new InvalidOperationException($"随机转换路径找不到实机原牌 {cardId}。");
        CardModel randomReplacement = transformSimulator.CreateRandomCardForTransform(
            deckCard,
            isInCombat: true,
            transformSimulator.Rng.CombatCardSelection);
        if (randomReplacement.Id.Entry == cardId)
            throw new InvalidOperationException(
                $"战斗内随机转换产出了同名牌 {cardId}，违反原版 GetFilteredTransformationOptions 的 Id 过滤。");
        CardChoiceSupport.TransformCardToGeneratedReplacement(transformSimulator, transformTarget, randomReplacement);
        // 断言对象必须是「进入原位置的替换牌」，不是被替换掉的原牌包装（后者按原版只是被移出战斗）。
        PredictedCard placed = transformState.FindCard(randomReplacement)
            ?? throw new InvalidOperationException($"转换替换牌 {randomReplacement.Id.Entry} 没有进入预测牌堆。");
        if (placed.Preview.DeckVersion is { } retained)
            throw new InvalidOperationException(
                $"战斗内转换替换保留了永久牌组身份（{retained.Id.Entry}），"
                + "与原版 CardModel.AfterCloned 清空 DeckVersion 的语义不符。");
        if (!ReferenceEquals(deckCard.DeckVersion, deckVersion))
            throw new InvalidOperationException($"随机转换改写了实机原牌 {cardId} 的永久牌组关联。");
        if (transformState.FindCard(deckCard) != null)
            throw new InvalidOperationException($"随机转换后预测根仍能找到已被替换的 {cardId} 原牌包装。");

        // —— 同名牌实例匹配：现场 D[6] 单字段差异的残余归因 ——
        // 现场抽牌堆里有两张同名牌：一张是牌组克隆（DeckVersion != null），一张是战斗生成副本（DeckVersion == null）；
        // 实机 D[6] 是克隆、预测缓存同一位置是副本，其余 30 张完全一致。
        // 机制：PlanCardToken（CombatPlan.cs:102-108）已经带 StateKey（ChoiceCardKey，含 |deck=），但
        //   MatchesToken（CardChoiceSupport.cs:1219-1225）只比对 CardId + UpgradeLevel，
        //   Find（1022-1030）用 Skip(token.SourceOccurrence) 在同名牌里取第 N 张，
        //   SourceOccurrence 由 CountTokenOccurrence（1008-1020，按当时的牌堆顺序）算出。
        // 于是同名牌的相对顺序一旦不同，计划 token 就会落到另一张实例上。
        CardModel liveCopy = allCards.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate, deckCard)
                && candidate.Id.Entry == cardId
                && candidate.DeckVersion == null)
            ?? throw new InvalidOperationException(
                $"同名牌实例匹配测试要求 {cardId} 的战斗副本（DeckVersion == null）。");

        var pairSimulator = new CombatPredictionSimulator(new SimulatedCombatState(combat));
        SimPlayerCombatState pairState = pairSimulator.State.GetPlayerCombatState(player);
        PredictedCard pairDeckCard = pairState.FindCard(deckCard)
            ?? throw new InvalidOperationException($"同名牌测试找不到 {cardId} 的牌组克隆。");
        PredictedCard pairCopy = pairState.FindCard(liveCopy)
            ?? throw new InvalidOperationException($"同名牌测试找不到 {cardId} 的战斗副本。");
        SimCardPile pairPile = pairDeckCard.GetPile(pairSimulator.State)
            ?? throw new InvalidOperationException($"同名牌测试找不到 {cardId} 所在牌堆。");
        if (!ReferenceEquals(pairCopy.GetPile(pairSimulator.State), pairPile))
            throw new InvalidOperationException($"同名牌测试要求 {cardId} 的克隆与副本位于同一牌堆。");

        int deckIndex = IndexOfCard(pairPile, pairDeckCard);
        int copyIndex = IndexOfCard(pairPile, pairCopy);
        if (deckIndex < 0 || copyIndex < 0)
            throw new InvalidOperationException($"同名牌测试找不到 {cardId} 的牌堆位置。");
        int slot = Math.Min(deckIndex, copyIndex);

        // 先按「副本在前、克隆在后」生成指向克隆的计划 token（SourceOccurrence = 1）。
        pairPile.Remove(pairDeckCard);
        pairPile.Remove(pairCopy);
        pairPile.Insert(slot, pairCopy);
        pairPile.Insert(slot + 1, pairDeckCard);
        int occurrenceOfDeckCard = CountSameNameBefore(pairPile, pairDeckCard);
        if (occurrenceOfDeckCard != 1)
            throw new InvalidOperationException(
                $"同名牌测试的前置顺序不成立：克隆的 SourceOccurrence={occurrenceOfDeckCard}，期望 1。");

        PlanCardToken tokenForDeckCard = new(
            cardId,
            pairDeckCard.Preview.CurrentUpgradeLevel,
            CardChoiceSupport.ChoiceCardKey(pairDeckCard),
            occurrenceOfDeckCard,
            occurrenceOfDeckCard,
            "同名牌牌组克隆");

        // 现在把两张牌的顺序换过来（克隆在前、副本在后），模拟回放时与生成计划时的顺序不同。
        pairPile.Remove(pairDeckCard);
        pairPile.Remove(pairCopy);
        pairPile.Insert(slot, pairDeckCard);
        pairPile.Insert(slot + 1, pairCopy);

        // Find 的等价筛选：只看 CardId + UpgradeLevel，再 Skip(SourceOccurrence)。
        List<PredictedCard> sameName = [.. pairPile.Cards.Where(card =>
            card.Preview.Id.Entry == tokenForDeckCard.CardId
            && card.Preview.CurrentUpgradeLevel == tokenForDeckCard.UpgradeLevel)];
        if (sameName.Count < 2)
            throw new InvalidOperationException($"同名牌测试要求牌堆里至少两张 {cardId}，实际 {sameName.Count}。");
        PredictedCard occurrencePick = sameName[tokenForDeckCard.SourceOccurrence];
        PredictedCard? stateKeyPick = pairPile.Cards.FirstOrDefault(
            card => CardChoiceSupport.ChoiceCardKey(card) == tokenForDeckCard.StateKey);

        // 证实一：序号匹配把计划指向的克隆换成了同名牌副本。
        if (!ReferenceEquals(occurrencePick, pairCopy))
            throw new InvalidOperationException(
                $"同名牌实例错位没有复现：序号匹配选中了 {(ReferenceEquals(occurrencePick, pairDeckCard) ? "牌组克隆" : "未知实例")}，"
                + $"期望在顺序变化后落到同名牌副本（token cardId={tokenForDeckCard.CardId} "
                + $"upgrade={tokenForDeckCard.UpgradeLevel} occurrence={tokenForDeckCard.SourceOccurrence} "
                + $"stateKey={tokenForDeckCard.StateKey}）。");

        // 证实二：token 自带的 StateKey 能选中计划真正指向的克隆。
        if (!ReferenceEquals(stateKeyPick, pairDeckCard))
            throw new InvalidOperationException(
                $"token 的 StateKey 没有选中计划指向的牌组克隆（cardId={tokenForDeckCard.CardId} "
                + $"stateKey={tokenForDeckCard.StateKey}）。");

        // 证实三：两张同名牌的状态键只差 |deck= 一位 —— 这正是现场「D[6] 一处差异、其余 30 张一致」的前提。
        string cloneKey = CardChoiceSupport.ChoiceCardKey(pairDeckCard);
        string copyKey = CardChoiceSupport.ChoiceCardKey(pairCopy);
        string cloneKeyAsCopy = cloneKey.Replace("|deck=True", "|deck=False", StringComparison.Ordinal);
        if (!string.Equals(cloneKeyAsCopy, copyKey, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "同名牌克隆与副本的状态键不止 |deck= 一位不同，现场的单点差异无法由错位单独解释："
                + $"clone={cloneKey} copy={copyKey}。");

        // 证实四：真实选牌回放同样把副本当成计划目标（token 指向克隆，回放移动的是副本）。
        CardChoiceSupport.Apply(
            pairSimulator,
            (SimulatedCombatState)pairSimulator.State.CombatState,
            pairCopy,
            new PlanCardChoice(PlanChoiceEffect.MoveToHand, pairPile.Type, [tokenForDeckCard]));
        bool copyLeftPile = !ReferenceEquals(pairCopy.GetPile(pairSimulator.State), pairPile);
        bool deckCardLeftPile = !ReferenceEquals(pairDeckCard.GetPile(pairSimulator.State), pairPile);
        if (!copyLeftPile || deckCardLeftPile)
            throw new InvalidOperationException(
                $"同名牌选牌回放没有复现错位：token 指向牌组克隆（StateKey={tokenForDeckCard.StateKey}，"
                + $"occurrence={tokenForDeckCard.SourceOccurrence}），回放却{(deckCardLeftPile ? "移动了克隆本身" : "没有移动任何一张")}。");

        // —— 非转换路径的清空写点排查（B016/T024 t11）——
        // 现场包没有任何战斗内转换事件，所以改用一条覆盖「弃牌—洗牌—抽牌」多轮往返的身份不变量来扫非转换路径：
        // 只要某条路径把既有牌当成生成牌（CreateCard / FromGenerated）或新克隆重建，它就会在该 wrapper 上写出
        // null 或另一个 DeckVersion 实例；这条链会把它读出来。
        var cycleSimulator = new CombatPredictionSimulator(new SimulatedCombatState(combat));
        SimPlayerCombatState cycleState = cycleSimulator.State.GetPlayerCombatState(player);
        List<(PredictedCard Card, CardModel Version, string Id)> traced =
        [.. cycleState.AllCards
            .Where(card => card.Preview.DeckVersion != null)
            .Select(card => (card, card.Preview.DeckVersion!, card.Preview.Id.Entry))];
        if (traced.Count == 0)
            throw new InvalidOperationException("非转换清空写点排查要求预测根里至少有一张带永久牌组身份的牌。");

        for (int round = 1; round <= 3; round++)
        {
            List<PredictedCard> hand = [.. cycleState.Hand.Cards];
            if (hand.Count > 0)
                cycleSimulator.Discard(hand);
            cycleSimulator.Draw(player, 5);
            cycleSimulator.Draw(player, 5);
            foreach ((PredictedCard card, CardModel version, string id) in traced)
            {
                if (card.GetPile(cycleSimulator.State) is null)
                    continue;
                if (ReferenceEquals(card.Preview.DeckVersion, version))
                    continue;
                string actual = card.Preview.DeckVersion is { } got ? got.Id.Entry : "null";
                throw new InvalidOperationException(
                    $"第 {round} 轮「弃牌—洗牌—抽牌」后 {id} 在非转换路径上丢了永久牌组身份："
                    + $"expected={version.Id.Entry}({version.GetHashCode()}) actual={actual}"
                    + $"（pile={card.GetPile(cycleSimulator.State)?.Type}）。");
            }
        }

        foreach (CardModel liveCard in allCards.Where(card => card.DeckVersion != null))
        {
            if (liveCard.DeckVersion is null)
                throw new InvalidOperationException($"预测侧改写了实机牌 {liveCard.Id.Entry} 的永久牌组关联。");
        }

        AssertSameNameIdentitySwapIsReported();

        _completedChecks.Add("CardCloneIdentity:DeckVersionContractSurvivesPileTransfers");
        _completedChecks.Add("CardCloneIdentity:InCombatTransformReplacementHasNoDeckVersion");
        _completedChecks.Add("CardCloneIdentity:DeckIdentitySurvivesPileCycle");
        _completedChecks.Add("CardCloneIdentity:SameNameTokenOccurrencePicksTheOtherInstance");
        _completedChecks.Add("CardCloneIdentity:StampReportsEveryDifferingSameNameIndex");

        // —— 跨回合 roll-out 的同名牌身份归属三分判定（B016/T024 t12）——
        // 现场：实机 turn 7 抽牌堆 D[6] 是牌组原牌 deck=True、D[24] 是战斗副本 deck=False，两张除身份位外逐字节相同。
        // roll-out 之后牌堆顺序会变，逐位置比较不再可用，所以按「实例归属」判定：
        // 分别追踪原牌包装与副本包装的 Preview.DeckVersion 引用，再给出三分结论。
        var rollout = new CombatPredictionSimulator(new SimulatedCombatState(combat));
        SimPlayerCombatState rolloutState = rollout.State.GetPlayerCombatState(player);
        if (rolloutState.FindCard(deckCard) is null || rolloutState.FindCard(liveCopy) is null)
            throw new InvalidOperationException($"跨回合 roll-out 起点缺少 {cardId} 的同名牌对。");

        var rolloutTrace = new List<string>
        {
            "start " + DescribeSameNameOwnership(rolloutState, cardId, deckCard, liveCopy),
        };
        for (int round = 1; round <= 6; round++)
        {
            List<PredictedCard> rolloutHand = [.. rolloutState.Hand.Cards];
            if (rolloutHand.Count > 0)
                rollout.Discard(rolloutHand);
            rollout.Draw(player, 5);
            rollout.Draw(player, 5);
            if (round is 3 or 6)
                rolloutTrace.Add($"round={round} " + DescribeSameNameOwnership(rolloutState, cardId, deckCard, liveCopy));
        }

        // 现场读数是抽牌堆里的位置，所以最后一个采样点把两张同名牌都送回抽牌堆再读一遍。
        List<PredictedCard> rollingHand = [.. rolloutState.Hand.Cards];
        if (rollingHand.Count > 0)
            rollout.Discard(rollingHand);
        rollout.Shuffle(player);
        rolloutTrace.Add("drawpile " + DescribeSameNameOwnership(rolloutState, cardId, deckCard, liveCopy));

        bool deckOwningWrapperPresent = TryFindOwnership(rolloutState, deckCard, out PredictedCard deckOwningWrapper);
        bool copyOwningWrapperPresent = TryFindOwnership(rolloutState, liveCopy, out PredictedCard copyOwningWrapper);
        bool deckCardKeptIdentity = deckOwningWrapperPresent
            && ReferenceEquals(deckOwningWrapper.Preview.DeckVersion, deckVersion);
        bool copyKeptNullIdentity = !copyOwningWrapperPresent || copyOwningWrapper.Preview.DeckVersion is null;
        bool copyStoleIdentity = copyOwningWrapperPresent && copyOwningWrapper.Preview.DeckVersion is not null;

        string rolloutVerdict = deckCardKeptIdentity && copyKeptNullIdentity
            ? "one-to-one"
            : copyStoleIdentity
                ? "ownership-swapped"
                : deckOwningWrapperPresent
                    ? "identity-dropped"
                    : "identity-owner-missing";

        _completedChecks.Add(
            $"CardCloneIdentity:RolloutOwnership={rolloutVerdict}"
            + $":deckcard={DescribeIdentityOwner(deckOwningWrapperPresent, deckOwningWrapper)}"
            + $":copy={DescribeIdentityOwner(copyOwningWrapperPresent, copyOwningWrapper)}");
        _completedChecks.Add("CardCloneIdentity:RolloutTrace=" + string.Join(" || ", rolloutTrace));
    }

    /// <summary>按实机牌对象找出预测侧仍在某个牌堆里的包装。</summary>
    private static bool TryFindOwnership(SimPlayerCombatState state, CardModel live, out PredictedCard card)
    {
        foreach (SimCardPile pile in state.AllPiles)
        {
            foreach (PredictedCard candidate in pile.Cards)
            {
                if (ReferenceEquals(candidate.Original, live) || ReferenceEquals(candidate.Preview, live))
                {
                    card = candidate;
                    return true;
                }
            }
        }
        card = null!;
        return false;
    }

    /// <summary>落盘一张包装的身份位与它引用的跑局版本（Id.Entry#HashCode）。</summary>
    private static string DescribeIdentityOwner(bool present, PredictedCard card)
    {
        if (!present)
            return "absent";
        return card.Preview.DeckVersion is { } version
            ? $"deck=true/{version.Id.Entry}#{version.GetHashCode()}"
            : "deck=false/null";
    }

    /// <summary>逐牌堆列出同名牌包装的归属、身份位与版本引用，用于 roll-out 落盘。</summary>
    private static string DescribeSameNameOwnership(
        SimPlayerCombatState state,
        string id,
        CardModel deckCard,
        CardModel copy)
    {
        List<string> parts = [];
        foreach (SimCardPile pile in state.AllPiles)
        {
            foreach (PredictedCard card in pile.Cards)
            {
                if (card.Preview.Id.Entry != id)
                    continue;
                string owner = ReferenceEquals(card.Original, deckCard) || ReferenceEquals(card.Preview, deckCard)
                    ? "deckcard"
                    : ReferenceEquals(card.Original, copy) || ReferenceEquals(card.Preview, copy)
                        ? "copy"
                        : "unregistered";
                string version = card.Preview.DeckVersion is { } value
                    ? $"{value.Id.Entry}#{value.GetHashCode()}"
                    : "null";
                parts.Add($"{pile.Type}/{owner}/deck={card.Preview.DeckVersion != null}/ver={version}");
            }
        }
        return parts.Count == 0 ? "none" : string.Join(',', parts);
    }

    // B016/T024 现场包 004.jsonl:58 只留下一条 field=D[6]：旧实现在牌堆字段里遇到第一个
    // 不同下标就 return，于是「预测侧丢了牌组身份」与「预测侧把身份记在另一个同名牌上」
    // 这两种成因给出完全相同的现场文本，无法裁决。这里锁死「逐处报出」这条诊断合同。
    private void AssertSameNameIdentitySwapIsReported()
    {
        const string body = "/False:0/False:-1/0/False/False/False/{0}/False/-:0:0/-:0[Cards=3,Damage=6,]/private=-/keywords=[]/baselib=-";
        string filler = "STRIKE_REGENT+0" + string.Format(body, "True");
        var expected = new ContinuationStamp(
            "turn=7;D=MAKE_IT_SO+0" + string.Format(body, "False") + "," + filler
            + ",MAKE_IT_SO+0" + string.Format(body, "True"));
        var actual = new ContinuationStamp(
            "turn=7;D=MAKE_IT_SO+0" + string.Format(body, "True") + "," + filler
            + ",MAKE_IT_SO+0" + string.Format(body, "False"));

        IReadOnlyList<string> differences = expected.DescribeDifferences(actual);
        if (differences.Count != 2)
            throw new InvalidOperationException(
                "同名牌身份互换必须同时报出两个下标，实际只报了 " + differences.Count + " 条："
                + string.Join(" | ", differences));
        if (!differences[0].Contains("field=D[0]", StringComparison.Ordinal)
            || !differences[1].Contains("field=D[2]", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "同名牌身份互换的差异下标不是 0 与 2：" + string.Join(" | ", differences));
    }

    private static int IndexOfCard(SimCardPile pile, PredictedCard card)
    {
        for (int index = 0; index < pile.Cards.Count; index++)
        {
            if (ReferenceEquals(pile.Cards[index], card))
                return index;
        }
        return -1;
    }

    private static int CountSameNameBefore(SimCardPile pile, PredictedCard target)
    {
        int occurrence = 0;
        foreach (PredictedCard card in pile.Cards)
        {
            if (ReferenceEquals(card, target))
                break;
            if (card.Preview.Id.Entry == target.Preview.Id.Entry
                && card.Preview.CurrentUpgradeLevel == target.Preview.CurrentUpgradeLevel)
                occurrence++;
        }
        return occurrence;
    }

    private static void RequireDeckVersion(
        PredictedCard card,
        CardModel expected,
        string cardId,
        string stage)
    {
        if (ReferenceEquals(card.Preview.DeckVersion, expected))
            return;
        string actual = card.Preview.DeckVersion is { } version
            ? version.Id.Entry + "(" + version.GetHashCode() + ")"
            : "null";
        throw new InvalidOperationException(
            $"{cardId} 在「{stage}」丢失了永久牌组身份："
            + $"expected={expected.Id.Entry}({expected.GetHashCode()}) actual={actual}。");
    }
}
