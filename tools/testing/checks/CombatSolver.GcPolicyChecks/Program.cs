using CombatSolver;

if (args is ["manual-release"])
    PolicyCheck.Run("manual memory release keeps live pages resident", GcManualMemoryReleaseChecks.Run);
else if (args is ["diagnostic-failure"])
    GcDiagnosticFailureChecks.Run();
else if (args is ["default-entry"])
    GcDiagnosticFailureChecks.RunDefaultEntry();
else if (args is ["default-commit"])
{
    PolicyCheck.Run("default-GC indivisible commit completes with CLR ownership", GcRecoveryChecks.RunDefaultScopeIndivisibleExit);
    PolicyCheck.Run("cancelled default-GC exit preserves the active bound", GcRecoveryChecks.RunCanceledDefaultScopeExit);
}
else if (args is ["recovery-lifecycle"])
{
    PolicyCheck.Run("actual region loss and bounded recovery", GcRecoveryChecks.RunLifecycle);
    PolicyCheck.Run("exit request invalidates pending recovery", GcRecoveryChecks.RunExitGuard);
    PolicyCheck.Run("explicit default-GC exit stays permanent", GcRecoveryChecks.RunExplicitDefaultExit);
}
else if (args is ["recovery"])
    GcRecoveryChecks.Run();
else if (args is ["memory"])
    PolicyCheck.Run("player trace memory accounting", GcMemoryBudgetChecks.Run);
else if (args is ["checkpoint"])
    PolicyCheck.Run("actual checkpoint resume and cancel", GcCheckpointChecks.Run);
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
    throw new ArgumentException("Expected no arguments, 'admission', 'scopes', 'checkpoint', 'diagnostic-failure', 'default-entry', 'default-commit', 'manual-release', 'memory', 'recovery' or 'recovery-lifecycle'.");
Console.WriteLine($"GC policy checks passed: {PolicyCheck.Completed} scenarios.");
