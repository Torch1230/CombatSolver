using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>Run rules frozen with a combat root and shared by its prediction branches.</summary>
internal sealed record CombatRulesSnapshot(GameMode GameMode, int AscensionLevel,
    ulong Ascensions, string? DominionId)
{
    public bool IsDread => GameMode == GameMode.Dread;
    public bool IsBlitz => GameMode == GameMode.Blitz;

    public static CombatRulesSnapshot Capture(CombatState combat)
        => Capture(combat.RunState, combat.Dominion?.Id.Entry);

    internal static CombatRulesSnapshot Capture(IRunState run, string? dominionId)
    {
        if (!MegaCrit.Sts2.Core.Nodes.NGame.IsMainThread())
            throw new InvalidOperationException("Capture live run rules on the main thread.");
        AscensionManager ascensions = new(run.GameMode, run.AscensionLevel);
        ulong mask = 0;
        foreach (AscensionLevel level in Enum.GetValues<AscensionLevel>())
            if (ascensions.HasLevel(level)) mask |= 1UL << (int)level;
        return new(run.GameMode, run.AscensionLevel, mask, dominionId);
    }

    public bool Allows(CardModel card)
        => !IsBlitz || card.IsAllowedInBlitz;

    public void Append(StringBuilder text)
        => text.Append(";combat_rules=").Append((int)GameMode).Append(',')
            .Append(AscensionLevel).Append(',').Append(Ascensions).Append(',').Append(DominionId);
}
