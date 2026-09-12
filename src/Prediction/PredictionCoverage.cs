using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PredictionCoverage
{
    public static IReadOnlyList<PredictionGap> Collect(CombatPredictionSimulator simulator)
    {
        return Normalize(simulator.History.Entries.OfType<CombatPredictionRiskEntry>().Select(ToGap));
    }

    internal static IReadOnlyList<PredictionGap> Normalize(IEnumerable<PredictionGap> gaps)
        => gaps.DistinctBy(gap => (gap.SourceId, gap.Method, gap.Reason, gap.Compensated))
            .OrderBy(gap => gap.SourceId, StringComparer.Ordinal)
            .ThenBy(gap => gap.Method, StringComparer.Ordinal)
            .ToList();

    private static PredictionGap ToGap(CombatPredictionRiskEntry entry)
    {
        AbstractModel? source = entry.Trace?.Source;
        string method = entry.Trace?.Invocation.Method?.Name
            ?? entry.Trace?.Invocation.Action?.ToString()
            ?? "Unknown";
        return FromSource(source, method, entry.Reason);
    }

    internal static PredictionGap FromSource(AbstractModel? source, string method, PredictionRiskReason reason)
    {
        string sourceId = source?.Id.Entry ?? source?.GetType().Name ?? "UNKNOWN";
        bool compensated = source switch
        {
            Armaments => true,
            AdaptablePower or CrabRagePower or DampenPower or IllusionPower or InfestedPower
                or PossessSpeedPower or PossessStrengthPower or RavenousPower or ReattachPower
                or SurprisePower or SurroundedPower when method == "AfterDeath" => true,
            SteamEruptionPower when method == "AfterDeath" => true,
            ConstrictPower when method == "AfterDeath" => true,
            HexPower when method == "AfterDeath" => true,
            ShrinkPower when method == "AfterDeath" => true,
            DecimillipedeSegment when method == "AfterDeath" => true,
            ConcoctPower when method == "AfterDamageGiven" => true,
            CorrosiveWavePower when method == "AfterCardDrawn" => true,
            CardModel card when method == "OnPlay"
                && (CardOnPlayCompensationCatalog.Contains(card) || CardEffectSpecRegistry.Contains(card)) => true,
            Inky when method == "OnPlay" => true,
            RelicModel relic when IsVerifiedNativeRelicHook(relic, method) => true,
            Enthralled or Normality when method == "ShouldPlay" => true,
            _ => false,
        };
        return new PredictionGap(sourceId, method, reason.ToString(), compensated);
    }

    private static bool IsVerifiedNativeRelicHook(RelicModel relic, string method)
        => relic switch
        {
            FakeStrikeDummy or MiniatureCannon or MysticLighter or StrikeDummy
                when method == "ModifyDamageAdditive" => true,
            SpikedGauntlets when method == "TryModifyEnergyCostInCombat" => true,
            TheBoot when method == "ModifyHpLostAfterOstyLate" => true,
            TungstenRod when method == "ModifyHpLostAfterOsty" => true,
            RuinedHelmet when method is "TryModifyPowerAmountReceived" or "AfterModifyingPowerAmountReceived" => true,
            UnsettlingLamp when method is "BeforePowerAmountChanged" or "ModifyPowerAmountGivenMultiplicative" => true,
            VitruvianMinion when method is "ModifyBlockMultiplicative" or "ModifyDamageMultiplicative" => true,
            _ => false,
        };
}
