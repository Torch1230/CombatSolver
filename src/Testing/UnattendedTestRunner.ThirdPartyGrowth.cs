using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string ThirdPartyGrowthTestId = "CombatSolver.Test.ThirdPartyGrowth";
    private const string ThirdPartyGrowthUnloadedId = "CombatSolver.Test.UnloadedMod";

    /// <summary>
    /// 第三方成长来源登记点（<see cref="GrowthSourceMirrors"/>）。这里自己登记一个来源、断言完
    /// 再撤销，所以同一个进程里后面的用例看不到它。
    /// </summary>
    private void AssertThirdPartyGrowthSources(CombatState combat)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Third-party growth: " + message);
        }
        static StateFingerprint Fingerprint(GrowthValues values)
        {
            StateFingerprintBuilder builder = new();
            values.AppendFingerprint(ref builder);
            return builder.Finish();
        }

        // 登记表为空时的负对照：指纹与只写原版来源时逐位相同（没有第三方段）。
        StateFingerprintBuilder vanillaOnly = new();
        for (int index = 0; index < Enum.GetValues<GrowthSource>().Length; index++)
            vanillaOnly.Add(0);
        Check(Fingerprint(default) == vanillaOnly.Finish(), "empty table leaves the fingerprint bit-identical");

        CardModel standIn = ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>();
        Check(!GrowthValues.HasTarget(standIn), "the stand-in card is not a vanilla growth target");
        Check(!GrowthSourceMirrors.IsRegistered(ThirdPartyGrowthTestId), "test source is not registered yet");

        GrowthSourceHandle source = GrowthSourceMirrors.Register(
            ThirdPartyGrowthTestId,
            () => standIn,
            card => card is MegaCrit.Sts2.Core.Models.Cards.DefendIronclad,
            static _ => "测试用第三方成长来源");
        GrowthSourceHandle unloaded = new(ThirdPartyGrowthUnloadedId);
        try
        {
            Check(GrowthSourceMirrors.IsRegistered(ThirdPartyGrowthTestId)
                && !GrowthSourceMirrors.IsRegistered(ThirdPartyGrowthUnloadedId), "registration is visible");
            Check(GrowthValues.HasTarget(standIn), "the registered predicate reaches HasTarget");
            GrowthOpportunityTargets legacyTarget = GrowthOpportunityPolicy.CaptureAvailableForTesting(
                [standIn.ToMutable()], 1);
            Check(!legacyTarget.IsBounded
                && legacyTarget.UnboundedSources.Any(target => target.SourceId == ThirdPartyGrowthTestId
                    && target.Reason == "target_not_registered"),
                "a legacy registration without a target calculator keeps full search");
            try
            {
                GrowthSourceMirrors.Register(ThirdPartyGrowthTestId, () => standIn, static _ => false);
                Check(false, "duplicate registration must throw");
            }
            catch (ArgumentException) { }
            try
            {
                default(GrowthValues).With(default(GrowthSourceHandle), 1);
                Check(false, "an uninitialized handle must throw");
            }
            catch (ArgumentException) { }

            GrowthValues vanilla = new(1, 2, 3, 4, 5, 6, 7, 8);
            GrowthValues budgets = vanilla.With(source, 11);
            Check(budgets.Get(source) == 11 && budgets.Total == 47, "the third-party budget joins Total");
            Check(budgets.With(source, 0) == vanilla, "clearing a third-party value returns the vanilla-only vector");
            Check(!default(GrowthValues).With(source, 0).IsEnabled && default(GrowthValues).With(source, 1).IsEnabled,
                "only a non-zero third-party value enables the policy");
            Check(Fingerprint(default(GrowthValues).With(source, 1)) != Fingerprint(default),
                "third-party counts enter the state fingerprint");
            Check(Fingerprint(default(GrowthValues).With(source, 1)) != Fingerprint(default(GrowthValues).With(unloaded, 1)),
                "two third-party sources with the same count are not the same state");
            try
            {
                default(GrowthValues).With(source, 1001).ValidateBudgets();
                Check(false, "an out-of-range third-party budget must throw");
            }
            catch (InvalidDataException) { }

            CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
            SimulatedCombatState parent = (SimulatedCombatState)simulator.State.CombatState;
            parent.RecordGrowthReward(source);
            CombatPredictionSimulator fork = simulator.Fork();
            SimulatedCombatState child = (SimulatedCombatState)fork.State.CombatState;
            child.RecordGrowthReward(source);
            Check(parent.GrowthRewards.Get(source) == 1 && child.GrowthRewards.Get(source) == 2,
                "fork event isolation");
            Check(budgets.Credit(parent.GrowthRewards) == 11 && budgets.Credit(child.GrowthRewards) == 22,
                "each third-party event earns its own budget");
            Check(vanilla.Credit(child.GrowthRewards) == 0, "an unbudgeted third-party source earns nothing");

            // 停用中的 mod：侧栏列不出这一行，但它那份额度不该被写回设置时冲掉。
            GrowthValues persisted = budgets.With(unloaded, 9);
            GrowthValues edited = default(GrowthValues).With(source, 5);
            Check(persisted.Extras.MergeUnregistered(edited.Extras)
                    == edited.With(unloaded, 9).Extras,
                "publishing keeps budgets for sources whose mod is not loaded");

            SolverSettingsData original = SolverSettings.Current;
            Check(SolverSettings.RoundTripForTesting(original with { GrowthBudgets = persisted }).GrowthBudgets == persisted,
                "third-party budgets round trip through settings, unknown ids included");
            try
            {
                SolverSettings.ApplyForTesting(original with { GrowthBudgets = persisted });
                using SolverGrowthStrategyPanel panel = new();
                Check(panel.ThirdPartyRowsForTesting.Count == 1
                    && panel.ThirdPartyRowsForTesting[0].Source == source, "the sidebar grows exactly one row");
                Check(panel.SettingsConfiguredForTesting, "the third-party row reloads its budget from settings");
            }
            finally { SolverSettings.ApplyForTesting(original); }
        }
        finally { GrowthSourceMirrors.UnregisterForTesting(source); }
        Check(!GrowthSourceMirrors.IsRegistered(ThirdPartyGrowthTestId), "test registration cleaned up");
        Check(!GrowthValues.HasTarget(standIn), "cleanup takes the predicate back out of HasTarget");

        GrowthOpportunityContext? frozenContext = null;
        GrowthSourceHandle boundedSource = GrowthSourceMirrors.Register(
            ThirdPartyGrowthTestId,
            () => standIn,
            card => card is MegaCrit.Sts2.Core.Models.Cards.DefendIronclad,
            opportunityTarget: context =>
            {
                frozenContext = context;
                return GrowthOpportunityTarget.Bounded(
                    context.MatchingCards.Sum(card => checked(1 + card.FixedReplayCount)));
            });
        try
        {
            CardModel replayed = standIn.ToMutable();
            CardCmd.Enchant<Spiral>(replayed, 1);
            GrowthOpportunityTargets bounded = GrowthOpportunityPolicy.CaptureAvailableForTesting([replayed], 2);
            Check(bounded.IsBounded && bounded.RequiredRewards.Get(boundedSource) == 2,
                "the optional calculator counts one physical card plus its fixed enchantment replay");
            Check(frozenContext is { EnemyCount: 2 }
                && frozenContext.MatchingCards is [{ CardId: "DEFEND_IRONCLAD", FixedReplayCount: 1 }],
                "the calculator receives only frozen root values");
            try
            {
                GrowthOpportunityTarget.Bounded(-1);
                Check(false, "negative target counts must throw");
            }
            catch (ArgumentOutOfRangeException) { }
        }
        finally { GrowthSourceMirrors.UnregisterForTesting(boundedSource); }

        GrowthSourceHandle invalidSource = GrowthSourceMirrors.Register(
            ThirdPartyGrowthTestId,
            () => standIn,
            card => card is MegaCrit.Sts2.Core.Models.Cards.DefendIronclad,
            opportunityTarget: static _ => default);
        try
        {
            try
            {
                GrowthOpportunityPolicy.CaptureAvailableForTesting([standIn.ToMutable()], 1);
                Check(false, "an uninitialized target result must reject policy capture");
            }
            catch (InvalidDataException) { }
        }
        finally { GrowthSourceMirrors.UnregisterForTesting(invalidSource); }
    }
}
