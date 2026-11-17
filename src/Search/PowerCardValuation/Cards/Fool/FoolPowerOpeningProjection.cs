using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 愚者开局能力投影（Specified）。旋律、射箭与悲伤的触发次数读取真实牌区，伤害上限为敌方剩余生命，
/// 格挡按来伤有界折算。投影只决定中途是否给能力路线保留席位，不进入终局比较。
/// 文件末尾是证据与投影共用的冻结事实：射箭牌按原版 CardTag.Shoot 识别，旋律牌按哇/啾/叽识别。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private const int FoolMaximumShortFightTurns = 5;

    private int FoolPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int energyUnit = PowerEnergyUnit(child);
        int enemies = Math.Max(1, child.Snapshot.AliveEnemyCount);
        int shoots = FoolCountTag(child, CardTag.Shoot);
        int melodies = FoolCountMelodyCards(child);
        int ennuiTurns = PowerPlayerPowerAmount<EnnuiPower>(child) > 0 ? turns : 0;
        return cardId switch
        {
            "ALL_IS_WELL" => PowerPerTriggerBlockPotential(
                PowerAmountGain<AllIsWellPower>(parent, child),
                FoolCountSadness(child, handOnly: false) + ennuiTurns,
                incoming),
            "ARCHERY" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<ArcheryPower>(parent, child),
                turns,
                1),
            "ECSTASY" => PowerPerTurnResourcePotential(
                PowerAmountGain<EcstasyPower>(parent, child) * energyUnit,
                Math.Min(turns, FoolCountSadness(child, handOnly: false) + ennuiTurns)),
            "ENNUI" => PowerPerTurnResourcePotential(
                PowerAmountGain<EnnuiPower>(parent, child) * energyUnit,
                Math.Min(turns, FoolMaximumShortFightTurns)),
            "EXPLOSIVE_ARROWS" => PowerPerTriggerDamagePotential(
                child,
                SaturatingProduct(PowerAmountGain<ExplosiveArrowsPower>(parent, child), enemies),
                shoots),
            "HIDDEN_ROOTS" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<HiddenRootsPower>(parent, child),
                turns,
                incoming),
            "HONING" => FoolPercentDamagePotential(
                child,
                FoolShootDamage(child, shoot: true),
                PowerAmountGain<HoningPower>(parent, child)),
            "LOST_LUTE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<LostLutePower>(parent, child) * energyUnit,
                melodies),
            "LULLABY" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<LullabyPower>(parent, child),
                Math.Max(1, Math.Min(turns + 1, melodies / SimulatedCombatState.MelodyTags.Length)),
                incoming),
            "MOTIF" => PowerPerTriggerResourcePotential(
                PowerAmountGain<MotifPower>(parent, child) * energyUnit,
                melodies),
            "MYTHIC_FORM" => PowerPerTurnResourcePotential(
                PowerAmountGain<MythicFormPower>(parent, child) * energyUnit,
                turns),
            "QUIVER" => PowerPerTurnDamagePotential(
                child,
                FoolMaxShootDamage(child),
                Math.Min(turns, Math.Max(0, shoots - 1)),
                1),
            "SERRATED_ARROWS" => FoolPercentDamagePotential(
                child,
                FoolShootDamage(child, shoot: false),
                PowerAmountGain<SerratedArrowsPower>(parent, child)),
            "SIMPLETON" => PowerPerTurnResourcePotential(
                PowerAmountGain<SimpletonPower>(parent, child) * energyUnit,
                turns),
            "SONG_PATH" => PowerPerTriggerBlockPotential(
                PowerAmountGain<SongPathPower>(parent, child),
                FoolCountTag(child, CardTag.Cho),
                incoming),
            "TUNE" => PowerPerTurnResourcePotential(
                PowerAmountGain<TunePower>(parent, child) * energyUnit,
                Math.Min(turns, FoolCountTag(child, CardTag.Ki))),
            "VIOLENT_CHORD" => PowerPerTriggerDamagePotential(
                child,
                SaturatingProduct(PowerAmountGain<ViolentChordPower>(parent, child), enemies),
                FoolCountTag(child, CardTag.Wah)),
            "YAH" => PowerPerTurnResourcePotential(
                PowerAmountGain<YahPower>(parent, child) * energyUnit,
                Math.Min(turns, shoots)),
            _ => 0,
        };
    }

    /// <summary>百分比增伤（磨砺、锯齿箭刃）：牌区内对应攻击的总伤害乘以增幅，上限为敌方剩余生命。</summary>
    private int FoolPercentDamagePotential(SearchNode child, int baseDamage, int percent)
        => Math.Min(
            PowerEnemyHp(child),
            (int)Math.Min(int.MaxValue, (long)Math.Max(0, baseDamage) * Math.Max(0, percent) / 100));

    // ---- 共用冻结事实：只读分支快照，不修改任何状态 ----

    private int FoolCountTag(SearchNode node, CardTag tag)
        => PowerLiveCards(node).Count(card => card.Preview.Tags.Contains(tag));

    private int FoolCountMelodyCards(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.Tags.Any(tag => tag is CardTag.Wah or CardTag.Cho or CardTag.Ki));

    private int FoolDistinctMelodyTags(SearchNode node)
    {
        PredictedCard[] cards = PowerLiveCards(node);
        int distinct = 0;
        foreach (CardTag tag in SimulatedCombatState.MelodyTags)
        {
            if (cards.Any(card => card.Preview.Tags.Contains(tag)))
                distinct++;
        }
        return distinct;
    }

    private int FoolCountSadness(SearchNode node, bool handOnly)
        => (handOnly ? PowerHandCards(node) : PowerLiveCards(node))
            .Count(card => card.Preview is Sadness);

    /// <summary>射箭牌单次打出的总伤害（伤害 × 重复），按牌区内全部仍可打出的射箭牌合计。</summary>
    private int FoolShootDamage(SearchNode node, bool shoot)
    {
        long total = 0;
        foreach (PredictedCard card in PowerLiveCards(node))
        {
            if (card.Preview.Type != CardType.Attack
                || card.Preview.Tags.Contains(CardTag.Shoot) != shoot
                || !card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar))
            {
                continue;
            }
            int hits = card.Preview.DynamicVars.TryGetValue("Repeat", out var repeatVar)
                ? Math.Max(1, repeatVar.IntValue)
                : 1;
            total += (long)Math.Max(0, damageVar.IntValue) * hits;
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private int FoolMaxShootDamage(SearchNode node)
        => PowerLiveCards(node)
            .Where(card => card.Preview.Tags.Contains(CardTag.Shoot)
                && card.Preview.DynamicVars.TryGetValue("Damage", out _))
            .Select(card => Math.Max(0, card.Preview.DynamicVars["Damage"].IntValue))
            .DefaultIfEmpty(0)
            .Max();

    private int FoolCountNonShootAttacks(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.Type == CardType.Attack && !card.Preview.Tags.Contains(CardTag.Shoot));
}
