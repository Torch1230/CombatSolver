using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 一个第三方成长来源的句柄。只能从
/// <see cref="GrowthSourceMirrors.Register(string, Func{CardModel}, Func{CardModel, bool}, Func{CardModel, string}, Func{GrowthOpportunityContext, GrowthOpportunityTarget})"/>
/// 取得，别自己拼——拼出来的 id 没有登记，额度永远是 0，侧栏里也不会出现。
/// </summary>
internal readonly record struct GrowthSourceHandle(string Id)
{
    public bool IsValid => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// 第三方"局外成长"来源的登记表。
///
/// <para>
/// 成长策略（<see cref="GrowthValues"/>）解决的是这类问题：贪婪之手、巨镰、遗传算法这些牌，
/// 收益落在**这场战斗之外**——金币、永久升级、局外强化。求解器默认只看本场战斗的血量与胜负，
/// 于是会把"多挨几点伤害换一次永久升级"判成亏。侧栏让玩家给每个来源单独填一份"每次收益允许的
/// 额外战损"，搜索据此在打分里给这条线路记一笔信用额度。
/// </para>
/// <para>
/// 原版那八个来源写死在 <see cref="GrowthSource"/> 里。局外成长类卡牌很多 mod 都有，它们全部
/// 落不进那个枚举：既拿不到自己的额度栏，收益也记不进 <see cref="SimulatedCombatState.GrowthRewards"/>，
/// 于是必然被判成"白挨伤害"。所以这里开一个登记点。
/// </para>
/// <para>
/// 登记之后，这个来源会：
/// <list type="bullet">
/// <item>在成长策略侧栏里多一行，有自己的图标、标题和额度输入框；</item>
/// <item>额度按登记时给的 id 存进设置文件，也进问题包的有效策略；</item>
/// <item>让 <c>HasGrowthTargets</c> 认得这张牌；未登记次数时保持完整搜索，登记有界次数时在兑现后允许早停；</item>
/// <item>可以用 <c>combat.RecordGrowthReward(handle)</c> 记一次实际到手的收益，进搜索的状态指纹。</item>
/// </list>
/// </para>
/// <para>
/// 登记表为空时，下游每一处都只多一次空判断：额度与计数向量的第三方部分是 <c>null</c>，
/// 指纹逐位相同，设置文件里也不会多出字段。
/// </para>
/// <para>
/// 求解器本身不认识任何第三方来源，登记由 mod 在加载时自己做。登记在初始化期间完成，
/// 任何搜索开始后保持登记表不变。
/// </para>
/// </summary>
internal static class GrowthSourceMirrors
{
    /// <summary>一条登记：id、取牌函数、标题覆盖、判据。</summary>
    internal readonly record struct Entry(
        string Id,
        Func<CardModel> Card,
        Func<CardModel, bool> HasTarget,
        Func<CardModel, string>? Title,
        Func<GrowthOpportunityContext, GrowthOpportunityTarget>? OpportunityTarget);

    private static readonly List<Entry> Entries = [];

    /// <summary>登记表是否为空。空表时下游可以整段跳过。</summary>
    public static bool IsEmpty => Entries.Count == 0;

    /// <summary>按登记顺序列出所有来源。侧栏按这个顺序排在原版八行之后。</summary>
    public static IReadOnlyList<Entry> All => Entries;

    /// <summary>
    /// 登记一个第三方成长来源。
    /// </summary>
    /// <param name="id">
    /// 这个来源的稳定标识，建议带上 mod 前缀（<c>"AutoWatcher.Diligence"</c> 这种形状）。
    /// 它是额度的持久化键：改了 id 等于换了一个来源，玩家原来填的额度会留在设置文件里但不再生效。
    /// 不得与已登记的 id 重复。
    /// </param>
    /// <param name="card">
    /// 取这个来源在侧栏里显示用的牌。**延迟调用**：登记发生在 mod 加载期，那时 <c>ModelDb</c>
    /// 可能还没准备好，所以这里传函数而不是牌。侧栏构建时调用一次。取不到牌不会连带侧栏起不来，
    /// 只是这一行退化成"没有图标、标题显示 id"，额度照样能填、照样生效。
    /// </param>
    /// <param name="hasTarget">
    /// 判一张牌算不算这个来源的目标。会对玩家牌组里每张牌调用，只做纯判断，别有副作用。
    /// 与原版 <see cref="GrowthValues.HasTarget"/> 同一口径：只要牌组里有一张算得上，
    /// 成长策略就算"有目标"。永久升级一类的来源记得跟原版一样要求
    /// <c>card.DeckVersion != null</c>——战斗里临时生成的副本升级了也带不出战斗。
    /// </param>
    /// <param name="title">
    /// 侧栏标题，参数是 <paramref name="card"/> 取回来的那张牌。
    /// 和 <paramref name="card"/> 一起延迟调用，所以里面可以放别的 <c>ModelDb</c> 查询。
    /// 留空用牌自己的名字；只有"牌名说明不了这个来源"的时候才填，
    /// 比如原版把黏稠强化那一行显示成"防御 + 强化名"。
    /// </param>
    /// <param name="opportunityTarget">
    /// 可选的本场最大兑现次数计算器。输入只有已冻结的匹配牌快照和敌人数，不能读取或修改实机战斗。
    /// 返回有界非负次数时允许在全部成长目标兑现后早停；返回不可证明或留空时继续完整搜索。
    /// </param>
    public static GrowthSourceHandle Register(
        string id,
        Func<CardModel> card,
        Func<CardModel, bool> hasTarget,
        Func<CardModel, string>? title = null,
        Func<GrowthOpportunityContext, GrowthOpportunityTarget>? opportunityTarget = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(hasTarget);
        foreach (Entry existing in Entries)
        {
            // 和别的镜像登记表同一口径：重复登记是错误，不静默覆盖。
            if (string.Equals(existing.Id, id, StringComparison.Ordinal))
                throw new ArgumentException($"第三方成长来源 {id} 已经登记过。", nameof(id));
        }
        Entries.Add(new Entry(id, card, hasTarget, title, opportunityTarget));
        return new GrowthSourceHandle(id);
    }

    /// <summary>
    /// 撤销一次登记。<b>只给无人测试用</b>：测试要自己登记一个来源、断言完再清干净，
    /// 否则同一个进程里后面的用例都会被这个来源影响。真实 mod 不要调用，
    /// 更不要在搜索进行中调用。
    /// </summary>
    internal static void UnregisterForTesting(GrowthSourceHandle source)
    {
        for (int index = 0; index < Entries.Count; index++)
        {
            if (string.Equals(Entries[index].Id, source.Id, StringComparison.Ordinal))
            {
                Entries.RemoveAt(index);
                return;
            }
        }
    }

    /// <summary>这个 id 登记过没有。</summary>
    public static bool IsRegistered(string id)
    {
        foreach (Entry entry in Entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>牌组里这张牌算不算某个已登记来源的目标。</summary>
    public static bool HasTarget(CardModel card)
    {
        if (Entries.Count == 0)
            return false;
        foreach (Entry entry in Entries)
        {
            if (entry.HasTarget(card))
                return true;
        }
        return false;
    }
}
