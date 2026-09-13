using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class PredictionCoverage
{
    internal static IReadOnlyList<PredictionGap> CollectBaselineForTesting(CombatPredictionSimulator simulator)
    {
        return simulator.History.Entries
            .OfType<CombatPredictionRiskEntry>()
            .Select(ToGapBaseline)
            .DistinctBy(gap => (gap.SourceId, gap.Method, gap.Reason, gap.Compensated))
            .OrderBy(gap => gap.SourceId, StringComparer.Ordinal)
            .ThenBy(gap => gap.Method, StringComparer.Ordinal)
            .ToList();
    }

    private static PredictionGap ToGapBaseline(CombatPredictionRiskEntry entry)
    {
        AbstractModel? source = entry.Trace?.Source;
        string sourceId = source?.Id.Entry ?? source?.GetType().Name ?? "UNKNOWN";
        string method = entry.Trace?.Invocation.Method?.Name
            ?? entry.Trace?.Invocation.Action?.ToString()
            ?? "Unknown";
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
        return new PredictionGap(sourceId, method, entry.Reason.ToString(), compensated);
    }

}
