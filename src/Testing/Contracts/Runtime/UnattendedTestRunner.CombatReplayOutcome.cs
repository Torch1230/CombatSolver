using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCombatReplayOutcomeOverkillAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await SetBlockAsync(player.Creature, 0);
        await CreatureCmd.SetMaxHp(player.Creature, 80);
        foreach (bool selfDamage in new[] { false, true })
        {
            foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            await InjectRelicAsync(player, new() { RelicId = "LIZARD_TAIL" });
            await CreatureCmd.SetCurrentHp(player.Creature, 6);
            using CombatReplayOutcome outcome = new(combat);
            await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, 9,
                ValueProp.Unpowered, selfDamage ? player.Creature : combat.Enemies.First());
            CombatReplayOutcomeSnapshot actual = outcome.Capture(combat, ended: false);
            if (actual.HpLost != 6 || actual.HpHealed != 40 || actual.FinalHp != 40
                || actual.UnattributedHpLoss != 0 || actual.SelfDamage != (selfDamage ? 6 : 0))
                throw new InvalidOperationException("Outcome must count actual HP loss once, separately from overkill and revival healing.");
            _completedChecks.Add($"CombatReplayOutcome:Overkill3:HpLost6:Revival40:SelfDamage={actual.SelfDamage}:Unattributed0");

            // A real HP change without a matching damage entry must still fail attribution.
            await CreatureCmd.SetCurrentHp(player.Creature, 39);
            actual = outcome.Capture(combat, ended: false);
            if (actual.HpLost != 7 || actual.UnattributedHpLoss != 1)
                throw new InvalidOperationException("Outcome must retain genuine unattributed HP loss.");
        }
        _completedChecks.Add("CombatReplayOutcome:UnknownHpLossRemainsExplicit");
    }
}
