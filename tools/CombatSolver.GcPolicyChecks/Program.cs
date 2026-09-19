using CombatSolver;

if (args is ["commit-window"])
    GcCommitWindowChecks.Run();
else if (args is ["recovery-lifecycle"])
{
    PolicyCheck.Run("actual region loss and bounded recovery", GcRecoveryChecks.RunLifecycle);
    PolicyCheck.Run("exit request invalidates pending recovery", GcRecoveryChecks.RunExitGuard);
}
else if (args is ["recovery"])
    GcRecoveryChecks.Run();
else if (args is ["memory"])
    PolicyCheck.Run("player trace memory accounting", GcMemoryBudgetChecks.Run);
else if (args is ["checkpoint"])
    PolicyCheck.Run("actual checkpoint resume and cancel", GcCheckpointChecks.Run);
else if (args is ["parallelism"])
    SearchParallelismControllerChecks.Run();
else if (args is ["scopes"])
    GcScopeLifecycleChecks.Run();
else if (args is ["admission"])
    GcRegionAdmissionChecks.Run();
else if (args.Length == 0)
{
    GcPolicyChecks.Run();
    GcRegionAdmissionChecks.Run();
}
else
    throw new ArgumentException("Expected no arguments, 'admission', 'parallelism', 'scopes', 'checkpoint', 'memory', 'recovery', 'recovery-lifecycle' or 'commit-window'.");
Console.WriteLine($"GC policy checks passed: {PolicyCheck.Completed} scenarios.");
