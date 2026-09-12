using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertThirdPartyCalculatedFailure(CombatState combat, Player player)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var live = player.PlayerCombatState!.Hand.Cards[0];
        var card = simulator.State.GetPlayerCombatState(player).FindCard(live)!;
        var previous = AssemblyInfo.MockTypes;
        AssemblyInfo.MockTypes = previous == null ? [] : new(previous);
        AssemblyInfo.MockTypes[live.GetType()] = (new Mod { path = "test-calculated", manifest = new ModManifest
            { id = "TestCalculatedMod", name = "Test Calculated Mod", affectsGameplay = true } }, false);
        try
        {
            try
            {
                new CalculatedVar("UnregisteredTestVar").InvokeCalculate(simulator, card, combat.Enemies[0]);
                throw new InvalidOperationException("Unsupported calculated card was accepted.");
            }
            catch (IncompatibleGameplayModException exception)
            {
                string message = SolverController.FormatSearchFailureForTesting(new InvalidOperationException("wrapper", exception), true);
                var ledger = new CombatBugReportIssueLedger();
                ledger.RecordFailure(CombatBugReportIssueKind.SearchSetupFailure, exception);
                if (exception.ModId != "TestCalculatedMod" || !exception.Subject.Contains(live.Id.Entry)
                    || !message.Contains("Test Calculated Mod") || message.Contains(SolverUiTokens.BugReportUploadInstruction)
                    || ledger.RequiresPlayerUpload)
                    throw new InvalidOperationException("Third-party calculated failure lost its source or requested upload.");
            }
            _completedChecks.Add("ThirdPartyCalculatedFailure:SourceClassified:WrappedUi:NoUploadPrompt");
        }
        finally { AssemblyInfo.MockTypes = previous; }
    }
}
