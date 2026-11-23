using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver.HotKernel;

/// <summary>
/// Standalone immutable run-rules snapshot frozen with a combat root and shared across all HotKernel branches.
/// </summary>
internal sealed record HotCombatRulesSnapshot(
    GameMode GameMode,
    int AscensionLevel,
    ulong Ascensions,
    string? DominionId)
{
    public bool IsDread => GameMode == GameMode.Dread;
    public bool IsBlitz => GameMode == GameMode.Blitz;

    public bool HasAscension(AscensionLevel level)
        => (Ascensions & (1UL << (int)level)) != 0;

    public static HotCombatRulesSnapshot Capture(CombatState combat)
        => Capture(combat.RunState, combat.Dominion?.Id.Entry);

    internal static HotCombatRulesSnapshot Capture(IRunState run, string? dominionId)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Capture live run rules on the main thread.");
        AscensionManager ascensions = new(run.GameMode, run.AscensionLevel);
        ulong mask = 0;
        foreach (AscensionLevel level in Enum.GetValues<AscensionLevel>())
        {
            if (ascensions.HasLevel(level))
                mask |= 1UL << (int)level;
        }
        return new HotCombatRulesSnapshot(run.GameMode, run.AscensionLevel, mask, dominionId);
    }

    public bool Allows(CardModel card)
        => !IsBlitz || card.IsAllowedInBlitz;

    public void Append(StringBuilder text)
        => text.Append(";combat_rules=").Append((int)GameMode).Append(',')
            .Append(AscensionLevel).Append(',').Append(Ascensions).Append(',').Append(DominionId);
}
