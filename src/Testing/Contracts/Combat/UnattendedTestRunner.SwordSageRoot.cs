using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertSwordSageRootBaselineAsync(CombatState combat, Player player)
    {
        // This extension is exercised by the dedicated SwordSage/SovereignBlade root fixture.
        if (player.Creature.GetPower<SwordSagePower>() is not { } livePower
            || player.PlayerCombatState!.AllCards.OfType<SovereignBlade>().FirstOrDefault() is not { } liveBlade)
            return;

        int capturedAmount = livePower.Amount;
        int capturedReplays = liveBlade.BaseReplayCount;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        try
        {
            livePower._amount = 0;
            CombatRootSnapshot zeroRoot = CombatRootSnapshot.Capture(combat);
            livePower._amount = capturedAmount + 7;
            await Task.Run(() =>
            {
                // Removing the captured bonus before the first normalization must still
                // visit root cards even though the current Power is already zero.
                CombatPredictionSimulator removed = root.ForkSimulator();
                SimulatedCombatState removedCombat = (SimulatedCombatState)removed.State.CombatState;
                removedCombat.SetAmount<SwordSagePower>(player.Creature, 0);
                removedCombat.NormalizePowerCardState(removed);
                if (removed.State.GetPlayerCombatState(player).FindCard(liveBlade)!.Preview.BaseReplayCount
                    != capturedReplays - capturedAmount)
                    throw new InvalidOperationException("Initial SwordSage removal skipped the frozen root bonus.");

                // Starting without the Power may skip work, but a later gain must still
                // apply to both existing and newly generated blades in this branch only.
                CombatPredictionSimulator zero = zeroRoot.ForkSimulator();
                SimulatedCombatState zeroCombat = (SimulatedCombatState)zero.State.CombatState;
                zeroCombat.NormalizePowerCardState(zero);
                PredictedCard zeroBlade = zero.State.GetPlayerCombatState(player).FindCard(liveBlade)!;
                if (zeroBlade.Preview.BaseReplayCount != capturedReplays)
                    throw new InvalidOperationException("Zero SwordSage normalization changed existing replays.");
                CombatPredictionSimulator gained = zero.Fork();
                SimulatedCombatState gainedCombat = (SimulatedCombatState)gained.State.CombatState;
                PredictedCard beforeGain = PredictedCard.Create(ModelDb.Card<SovereignBlade>(), player);
                int beforeGainBase = beforeGain.Preview.BaseReplayCount;
                gained.AddToPile(beforeGain, PileType.Hand);
                gainedCombat.NormalizePowerCardState(gained);
                gainedCombat.SetAmount<SwordSagePower>(player.Creature, 3);
                gainedCombat.NormalizePowerCardState(gained);
                PredictedCard gainedBlade = gained.State.GetPlayerCombatState(player).FindCard(liveBlade)!;
                if (gainedBlade.Preview.BaseReplayCount != capturedReplays + 3
                    || beforeGain.Preview.BaseReplayCount != beforeGainBase + 3
                    || zeroBlade.Preview.BaseReplayCount != capturedReplays)
                    throw new InvalidOperationException("SwordSage gain after skipped normalization lost its zero baseline or Fork isolation.");
                gainedCombat.SetAmount<SwordSagePower>(player.Creature, 0);
                gainedCombat.NormalizePowerCardState(gained);
                if (gainedBlade.Preview.BaseReplayCount != capturedReplays
                    || beforeGain.Preview.BaseReplayCount != beforeGainBase)
                    throw new InvalidOperationException("SwordSage removal skipped an earlier nonzero bonus.");

                CombatPredictionSimulator parent = root.ForkSimulator();
                SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
                PredictedCard blade = parent.State.GetPlayerCombatState(player).FindCard(liveBlade)!;
                parentCombat.NormalizePowerCardState(parent);
                if (blade.Preview.BaseReplayCount != capturedReplays)
                    throw new InvalidOperationException("SwordSage normalization read the changed live amount after root capture.");

                parentCombat.SetAmount<SwordSagePower>(player.Creature, capturedAmount + 2);
                parentCombat.NormalizePowerCardState(parent);
                if (blade.Preview.BaseReplayCount != capturedReplays + 2)
                    throw new InvalidOperationException("SwordSage did not apply the branch amount delta.");

                CombatPredictionSimulator child = parent.Fork();
                SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
                PredictedCard childBlade = child.State.GetPlayerCombatState(player).FindCard(liveBlade)!;
                childCombat.SetAmount<SwordSagePower>(player.Creature, 0);
                childCombat.NormalizePowerCardState(child);
                if (childBlade.Preview.BaseReplayCount != capturedReplays - capturedAmount
                    || blade.Preview.BaseReplayCount != capturedReplays + 2)
                    throw new InvalidOperationException("SwordSage removal crossed the parent/child boundary.");

                PredictedCard generated = PredictedCard.Create(ModelDb.Card<SovereignBlade>(), player);
                int generatedBase = generated.Preview.BaseReplayCount;
                parent.AddToPile(generated, PileType.Hand);
                parentCombat.NormalizePowerCardState(parent);
                parentCombat.NormalizePowerCardState(parent);
                if (generated.Preview.BaseReplayCount != generatedBase + capturedAmount + 2)
                    throw new InvalidOperationException("A generated blade used the root-card baseline or was normalized twice.");
            });
        }
        finally
        {
            livePower._amount = capturedAmount;
        }
        if (liveBlade.BaseReplayCount != capturedReplays)
            throw new InvalidOperationException("SwordSage root verification mutated the live blade.");
        Entry.Logger.Info("[CombatSolver/Unattended] SWORD_SAGE_ROOT_BASELINE_OK frozen_live branch_delta removal fork generated idempotence initial_removal zero_then_gain");
    }
}
