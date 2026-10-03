using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.RichTextTags;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool OriginalSine(RichTextSine effect, CharFXTransform transform) => throw new NotSupportedException();
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool OriginalFlyIn(RichTextFlyIn effect, CharFXTransform transform) => throw new NotSupportedException();

    private async Task AssertCombatTimingLifetimeAsync(CombatState combat, Player player)
    {
        Harmony harmony = new("combat_solver.timing_contract");
        foreach (var pair in new[] { (typeof(RichTextSine), nameof(OriginalSine)), (typeof(RichTextFlyIn), nameof(OriginalFlyIn)) })
            harmony.CreateReversePatcher(AccessTools.Method(pair.Item1, "_ProcessCustomFX"),
                new HarmonyMethod(AccessTools.Method(typeof(UnattendedTestRunner), pair.Item2))).Patch(HarmonyReversePatchType.Original);
        var prefs = SaveManager.Instance.PrefsSave;
        bool textEffects = prefs.TextEffectsEnabled;
        var settings = SolverSettings.Current;
        var speed = prefs.FastMode;
        try
        {
            using RichTextSine sine = new();
            using RichTextFlyIn fly = new();
            using Godot.Collections.Dictionary env = new() { ["offset_x"] = 0.5, ["offset_y"] = 0.2, ["visible"] = false, ["color"] = Colors.Red };
            using CharFXTransform actual = new();
            using CharFXTransform expected = new();
            foreach (bool enabled in new[] { true, false })
            foreach (bool flyIn in new[] { true, false })
            foreach (double time in new[] { 0.0, 0.25, 2.0 })
            {
                prefs.TextEffectsEnabled = enabled;
                foreach (var fx in new[] { actual, expected })
                {
                    fx.Env = env;
                    fx.ElapsedTime = time;
                    fx.RelativeIndex = 3;
                    fx.Color = Colors.White;
                    fx.Offset = Vector2.Zero;
                    fx.Transform = Transform2D.Identity;
                    fx.Visible = true;
                }
                bool baseline = flyIn ? OriginalFlyIn(fly, expected) : OriginalSine(sine, expected);
                bool patched = flyIn ? fly._ProcessCustomFX(actual) : sine._ProcessCustomFX(actual);
                if (baseline != patched || actual.Color != expected.Color || actual.Offset != expected.Offset
                    || actual.Transform != expected.Transform || actual.Visible != expected.Visible)
                    throw new InvalidOperationException("Rich text callback differs from native output.");
            }
            prefs.TextEffectsEnabled = true;
            Type tracker = typeof(GodotObject).Assembly.GetType("Godot.DisposablesTracker", throwOnError: true)!;
            object entries = AccessTools.Property(tracker, "OtherInstances").GetValue(null)!;
            PropertyInfo count = entries.GetType().GetProperty("Count")!;
            int before = (int)count.GetValue(entries)!;
            for (int i = 0; i < 20000; i++)
            {
                sine._ProcessCustomFX(actual);
                fly._ProcessCustomFX(actual);
            }
            int after = (int)count.GetValue(entries)!;
            if (after > before + 10 || env.Count != 4)
                throw new InvalidOperationException($"Rich text disposable registrations accumulated: {before} -> {after}.");
            _completedChecks.Add($"NativeTextEffectsEquivalent:40000Callbacks:Registrations={before}->{after}");

            prefs.FastMode = FastModeType.Normal;
            SolverSettings.ApplyForTesting(settings with { DeploymentFastMode = SolverDeploymentFastMode.Instant });
            if (CombatInstantModePatch.Resolve(prefs) != FastModeType.Instant || prefs.FastMode != FastModeType.Normal)
                throw new InvalidOperationException("Combat instant override mutated the preference or was inactive.");
            Task instantWait = Cmd.Wait(0.15f, ignoreCombatEnd: true);
            if (!instantWait.IsCompletedSuccessfully) throw new InvalidOperationException("Native combat wait did not run instantly.");
            SolverSettings.ApplyForTesting(settings with { DeploymentFastMode = SolverDeploymentFastMode.FollowGame });
            Task normalWait = Cmd.Wait(0.15f, ignoreCombatEnd: true);
            if (normalWait.IsCompleted) throw new InvalidOperationException("Native normal wait was bypassed.");
            await normalWait;
            SolverSettings.ApplyForTesting(settings with { DeploymentFastMode = SolverDeploymentFastMode.Instant });
            int turn = player.PlayerCombatState!.TurnNumber;
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            if (prefs.FastMode != FastModeType.Normal || CombatInstantModePatch.Resolve(prefs) != FastModeType.Instant)
                throw new InvalidOperationException("Combat speed did not survive the enemy turn with the original preference intact.");
            _completedChecks.Add("NativeInstantAndNormalWait:EnemyTurnCompleted:PreferenceUnchanged");
            foreach (var enemy in combat.Enemies.ToArray()) await CreatureCmd.Kill(enemy);
            await CombatManager.Instance.CheckWinCondition();
            while (CombatManager.Instance.IsInProgress)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            if (CombatInstantModePatch.Resolve(prefs) != FastModeType.Normal || prefs.FastMode != FastModeType.Normal)
                throw new InvalidOperationException("Instant speed escaped combat scope.");
            Task outsideWait = Cmd.Wait(0.15f, ignoreCombatEnd: true);
            if (outsideWait.IsCompleted) throw new InvalidOperationException("Out-of-combat native wait was accelerated.");
            await outsideWait;
            _completedChecks.Add("CombatEnded:NativeOutsideWaitAndPreferenceRemainNormal");
        }
        finally
        {
            prefs.TextEffectsEnabled = textEffects;
            prefs.FastMode = speed;
            SolverSettings.ApplyForTesting(settings);
        }
    }
}
