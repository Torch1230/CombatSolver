using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Cards.DynamicVars;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static PowerModel? _ownerWarmupProbe;
    private static int _ownerWarmupCalls;

    private static bool OwnerBoundCanonicalVarsProbe(PowerModel __instance,
        ref IEnumerable<MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> __result)
    {
        if (!ReferenceEquals(__instance, _ownerWarmupProbe)) return true;
        _ownerWarmupCalls++;
        __result = [new MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar("OwnerHp", __instance.Owner.CurrentHp)];
        return false;
    }

    private async Task AssertRootContentSourcesAsync(CombatState combat, Player player)
    {
        var previousMocks = AssemblyInfo.MockTypes;
        string language = LocManager.Instance.Language;
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        Mod mod = new() { path = "test-content", manifest = new ModManifest
            { id = "TestContentMod", name = "Content [Test]", affectsGameplay = true } };
        try
        {
            AssertKnownGameplayModBoundary();
            MonsterModel monster = combat.Enemies[0].Monster!;
            AssemblyInfo.MockTypes[monster.GetType()] = (mod, false);
            AssertBoundary(() => PredictionModPatchAudit.ValidateMonsterModels([monster]));
            AssemblyInfo.MockTypes.Remove(monster.GetType());
            var attack = new MegaCrit.Sts2.Core.MonsterMoves.Intents.SingleAttackIntent((Func<decimal>)null!);
            AssemblyInfo.MockTypes[attack.GetType()] = (mod, false);
            AssertBoundary(() => IntentForecaster.CaptureBaseDamage(attack, monster, "CHOMP_MOVE"));
            AssemblyInfo.MockTypes.Remove(attack.GetType());
            AssemblyInfo.MockTypes[typeof(ForeignCardPatch)] = (mod, false);
            HarmonyLib.Harmony intentProbe = new("CombatSolver.Testing.IntentSource." + _request.RunId);
            var constructor = HarmonyLib.AccessTools.Constructor(attack.GetType(), [typeof(int)]);
            var generate = HarmonyLib.AccessTools.Method(monster.GetType(), "GenerateMoveStateMachine");
            try
            {
                intentProbe.Patch(constructor, prefix: new HarmonyLib.HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));
                AssertBoundary(() => IntentForecaster.CaptureBaseDamage(attack, monster, "CHOMP_MOVE"));
                intentProbe.Patch(generate, prefix: new HarmonyLib.HarmonyMethod(typeof(ForeignCardPatch), nameof(ForeignCardPatch.Prefix)));
                AssertBoundary(() => PredictionModPatchAudit.ValidateMonsterModels([monster]));
            }
            finally { intentProbe.UnpatchAll(intentProbe.Id); AssemblyInfo.MockTypes.Remove(typeof(ForeignCardPatch)); }
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            CardModel card = player.PlayerCombatState!.Hand.Cards.First();
            AssemblyInfo.MockTypes[card.GetType()] = (mod, false);
            shadow.SetPendingTurnStartChoice(new("STRATAGEM_POWER", PlanChoiceEffect.MoveToHand, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, 1));
            using (simulator.PushActionSource(card, PredictionActionKind.CardPlay))
            {
                if (!TurnStartChoiceSupport.ResolvePileDiscard(simulator, shadow, player, null,
                        card.Id.Entry, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw, []))
                    throw new InvalidOperationException("An empty choice operation must complete without replacing the pending choice.");
                AssertBoundary(() => TurnStartChoiceSupport.ResolvePileDiscard(simulator, shadow, player, null,
                    card.Id.Entry, MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw,
                    simulator.State.GetPlayerCombatState(player).DrawPile.Cards));
            }
            if (shadow.PendingTurnStartChoice?.SourceId != "STRATAGEM_POWER")
                throw new InvalidOperationException("Callback overlap changed the pending choice.");
            AssemblyInfo.MockTypes.Remove(card.GetType());

            await InjectPowerAsync(combat, player, new() { PowerId = "STRENGTH_POWER", Target = "Player", Amount = 1 });
            PowerModel canonical = ModelDb.Power<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>();
            PowerModel attached = player.Creature.GetPower<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>()!;
            var varsField = HarmonyLib.AccessTools.Field(typeof(PowerModel), "_dynamicVars");
            object? canonicalVars = varsField.GetValue(canonical);
            object? attachedVars = varsField.GetValue(attached);
            var getter = HarmonyLib.AccessTools.PropertyGetter(typeof(PowerModel), "CanonicalVars");
            HarmonyLib.Harmony probe = new("CombatSolver.Testing.OwnerWarmup." + _request.RunId);
            probe.Patch(getter, prefix: new HarmonyLib.HarmonyMethod(typeof(UnattendedTestRunner), nameof(OwnerBoundCanonicalVarsProbe)));
            AssemblyInfo.MockTypes[canonical.GetType()] = (mod, false);
            try
            {
                varsField.SetValue(canonical, null);
                _ownerWarmupProbe = canonical;
                _ownerWarmupCalls = 0;
                PowerDynamicVarWarmup.EnsureCanonicalMaterialized([canonical]);
                if (_ownerWarmupCalls != 0) throw new InvalidOperationException("Owner-dependent canonical Power was materialized.");
                varsField.SetValue(attached, null);
                _ownerWarmupProbe = attached;
                PowerDynamicVarWarmup.EnsureMaterialized(combat);
                if (_ownerWarmupCalls != 1 || attached.DynamicVars["OwnerHp"].IntValue != player.Creature.CurrentHp)
                    throw new InvalidOperationException("Attached owner-dependent Power was not materialized.");
            }
            finally
            {
                _ownerWarmupProbe = null;
                probe.UnpatchAll(probe.Id);
                varsField.SetValue(canonical, canonicalVars);
                varsField.SetValue(attached, attachedVars);
            }
            _completedChecks.Add("RootContent:KnownMod:Monster:MissingAttackDamage:CallbackOverlap:OwnerBoundPower:Source:UploadClassification");
        }
        finally { AssemblyInfo.MockTypes = previousMocks; LocManager.Instance.SetLanguage(language); }

        static void AssertBoundary(Action action)
        {
            try { action(); throw new InvalidOperationException("Content boundary was accepted."); }
            catch (IncompatibleGameplayModException failure)
            {
                if (failure.ModId != "TestContentMod") throw new InvalidOperationException("Content source was lost.", failure);
                AssertContentFailurePresentation(failure);
            }
        }
    }

    private sealed class UnadaptedContentModel : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => false;
        public int Calls;
        public override Task BeforeSideTurnStart(PlayerChoiceContext context, CombatSide side,
            IReadOnlyList<Creature> participants, ICombatState combat) { Calls++; return Task.CompletedTask; }
        public override Task AfterPlayerTurnStartEarly(PlayerChoiceContext context, Player player)
            { Calls++; return Task.CompletedTask; }
        public override Task AfterPlayerTurnStart(PlayerChoiceContext context, Player player)
            { Calls++; return Task.CompletedTask; }
        public override Task AfterPlayerTurnStartLate(PlayerChoiceContext context, Player player)
            { Calls++; return Task.CompletedTask; }
        public override Task AfterSideTurnEndLate(PlayerChoiceContext context, CombatSide side,
            IEnumerable<Creature> participants) { Calls++; return Task.CompletedTask; }
        public override decimal ModifyGoldGained(Player player, decimal amount) { Calls++; return amount; }
        public override Task AfterModifyingGoldGained(Player player, decimal amount) { Calls++; return Task.CompletedTask; }
        public override Task AfterGoldGained(Player player) { Calls++; return Task.CompletedTask; }
    }

    private void AssertContentModFailures(CombatState combat, Player player)
    {
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var model = ModelDb.All.OfType<UnadaptedContentModel>().Single();
        var previousMocks = AssemblyInfo.MockTypes;
        string previousLanguage = LocManager.Instance.Language;
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        var mod = new Mod { path = "test-content", manifest = new ModManifest
            { id = "TestContentMod", name = "Content [Test]", affectsGameplay = true } };
        AssemblyInfo.MockTypes[typeof(UnadaptedContentModel)] = (mod, false);
        try
        {
            var beforeStart = new BeforeSideTurnStartMirrorContext
                { Simulator = simulator, Side = CombatSide.Player, Participants = [player.Creature] };
            var playerStart = new AfterPlayerTurnStartMirrorContext
                { Simulator = simulator, Player = player, Choices = new([]) };
            var afterEnd = new AfterSideTurnEndLateMirrorContext
                { Simulator = simulator, Side = CombatSide.Player, Participants = [player.Creature] };
            var gold = new GoldGainMirrorContext { Simulator = simulator, Player = player, Amount = 10m };
            foreach (Action action in new Action[]
            {
                () => BeforeSideTurnStartMirrors.Invoke(model, beforeStart),
                () => AfterPlayerTurnStartMirrors.Invoke(model, playerStart, 0),
                () => AfterPlayerTurnStartMirrors.Invoke(model, playerStart, 1),
                () => AfterPlayerTurnStartMirrors.Invoke(model, playerStart, 2),
                () => AfterSideTurnEndLateMirrors.Invoke(model, afterEnd),
                () => _ = GoldGainedMirrors.Modify(model, gold),
                () => GoldGainedMirrors.AfterModify(model, gold),
                () => GoldGainedMirrors.AfterGain(model, gold),
            })
            {
                try
                {
                    action();
                    throw new InvalidOperationException("Unadapted content effect was accepted.");
                }
                catch (IncompatibleGameplayModException failure)
                {
                    if (failure.ModId != "TestContentMod"
                        || !failure.Subject.Contains(typeof(UnadaptedContentModel).FullName!, StringComparison.Ordinal))
                        throw new InvalidOperationException("Content failure lost its model and mod source.", failure);
                    AssertContentFailurePresentation(failure);
                }
            }
            if (model.Calls != 0)
                throw new InvalidOperationException("Unadapted native content hooks were executed.");

            CardModel live = player.PlayerCombatState!.Hand.Cards[0];
            var card = simulator.State.GetPlayerCombatState(player).FindCard(live)!;
            var computed = new TrackingComputedDynamicVar();
            AssemblyInfo.MockTypes[typeof(TrackingComputedDynamicVar)] = (mod, false);
            try
            {
                computed.InvokeCalculate(simulator, card, combat.Enemies[0]);
                throw new InvalidOperationException("Unadapted computed variable was accepted.");
            }
            catch (IncompatibleGameplayModException failure) { AssertContentFailurePresentation(failure); }
            if (computed.CalculateCalled)
                throw new InvalidOperationException("Unadapted native variable evaluator was executed.");

            Exception frameworkFailure;
            try
            {
                var sharedWrapper = new ComputedDynamicVar("Damage", 17m, _ => 99m);
                ((IComputedDynamicVar)sharedWrapper).InvokeCalculate(simulator, card, combat.Enemies[0]);
                throw new InvalidOperationException("Unadapted framework calculation was accepted.");
            }
            catch (PredictionUnsupportedException failure) { frameworkFailure = failure; }

            foreach (Exception failure in new Exception[]
            {
                frameworkFailure,
                PredictionUnsupportedException.ForContent("native content missing", live.GetType()),
                new PlatformNotSupportedException("platform failure"),
                new InvalidOperationException("ordinary search failure"),
            })
            {
                var ledger = new CombatBugReportIssueLedger();
                ledger.RecordFailure(CombatBugReportIssueKind.SearchFailure, new InvalidOperationException("wrapper", failure));
                if (failure is IncompatibleGameplayModException || !ledger.RequiresPlayerUpload
                    || !SolverController.FormatSearchFailureForTesting(failure, false)
                        .Contains(SolverUiTokens.BugReportUploadInstruction, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native or runtime failure must retain its diagnostic upload prompt.");
                if (failure is PlatformNotSupportedException
                    && ledger.Snapshot().Any(issue => issue.Kind == CombatBugReportIssueKind.UnsupportedCombatSemantic))
                    throw new InvalidOperationException("Platform failure was classified as a combat effect.");
            }
            _completedChecks.Add("ContentModFailures:EightHooks:ComputedVar:Source:WrappedFailure:eng-zhs-zht:UploadClassification");
        }
        finally
        {
            LocManager.Instance.SetLanguage(previousLanguage);
            AssemblyInfo.MockTypes = previousMocks;
        }
    }

    private static void AssertContentFailurePresentation(IncompatibleGameplayModException failure)
    {
        var wrapped = new InvalidOperationException("search wrapper", failure);
        foreach (string language in new[] { "eng", "zhs", "zht" })
        {
            LocManager.Instance.SetLanguage(language);
            string expected = language == "eng"
                ? "The solver does not yet support this content mod: Content ［Test］（TestContentMod）. This combat cannot be solved."
                : "求解器暂未适配此内容性 Mod：Content ［Test］（TestContentMod），无法求解。";
            foreach (string text in new[]
            {
                SolverController.FormatSearchSetupFailure(wrapped),
                SolverController.FormatSearchFailureForTesting(wrapped, true),
            })
                if (!text.Contains(expected, StringComparison.Ordinal)
                    || text.Contains(SolverUiTokens.BugReportUploadInstruction, StringComparison.Ordinal)
                    || text.Contains(SolverUiTokens.ParallelSearchFailureInstruction, StringComparison.Ordinal)
                    || text.Contains(failure.Subject, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Content mod prompt is incorrect: {language}.");
        }
        foreach (var primary in new[] { CombatBugReportIssueKind.SearchSetupFailure, CombatBugReportIssueKind.SearchFailure,
            CombatBugReportIssueKind.DeploymentFailure, CombatBugReportIssueKind.TurnSetupFailure })
        {
            var ledger = new CombatBugReportIssueLedger();
            ledger.RecordFailure(primary, wrapped);
            if (ledger.RequiresPlayerUpload || ledger.Snapshot().Single().Kind != CombatBugReportIssueKind.IncompatibleGameplayMod)
                throw new InvalidOperationException("Content failure requested a bug report.");
        }
    }
}
