using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    private readonly record struct PrimaryChoiceMatch(
        string ContextId,
        PlanChoiceEffect Effect,
        PileType SourcePile,
        int MinCount);

    private readonly record struct PendingChoiceReplayBranch(
        PlanAction Action,
        bool PruneInvalidBranch);

    private sealed record PrimaryCardChoiceLayer(
        IReadOnlyList<PlanCardChoice?> Choices,
        bool UnregisteredPendingChoice,
        int SemanticBranchCount,
        bool IdentityChangingLayer,
        WholeActionChoiceBudget WholeActionBudget);

    private sealed record PendingChoiceReplayLayer(
        IReadOnlyList<PendingChoiceReplayBranch> Branches,
        ExecutionChoiceReplayCheckpoint? Checkpoint = null) : IDisposable
    {
        public void Dispose() => Checkpoint?.Dispose();
    }

    private sealed record TurnSetupChoiceLayer(
        TurnStartChoiceRequest Request,
        CardChoiceSpec Spec,
        IReadOnlyList<PlanCardChoice> Branches);

    private sealed record PendingChoiceBudgetSeed(
        CardChoiceSpec? Spec,
        int SemanticBranchCount,
        TurnSetupChoiceLayer? TurnSetupLayer);

    /// <summary>
    /// A hierarchical lease over one exclusively owned whole-action budget. Child leases cap how
    /// much an earlier stable branch may consume while reserving one unit for every later sibling;
    /// actual consumption is also charged to every ancestor. Unused quota is therefore never
    /// stranded in an invalid or shallow branch, while traversal stays deterministic.
    /// </summary>
    private sealed class ChoiceSearchBudget
    {
        private readonly ChoiceSearchBudget? _parent;
        private int _semanticFinalQuota;
        private int _materializedOccurrenceFinalQuota;
        private int _replayAttemptQuota;

        public ChoiceSearchBudget(
            int semanticFinalQuota,
            int materializedOccurrenceFinalQuota,
            int replayAttemptQuota)
            : this(
                parent: null,
                semanticFinalQuota,
                materializedOccurrenceFinalQuota,
                replayAttemptQuota)
        {
        }

        private ChoiceSearchBudget(
            ChoiceSearchBudget? parent,
            int semanticFinalQuota,
            int materializedOccurrenceFinalQuota,
            int replayAttemptQuota)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(semanticFinalQuota);
            ArgumentOutOfRangeException.ThrowIfNegative(materializedOccurrenceFinalQuota);
            ArgumentOutOfRangeException.ThrowIfNegative(replayAttemptQuota);
            _parent = parent;
            _semanticFinalQuota = semanticFinalQuota;
            _materializedOccurrenceFinalQuota = materializedOccurrenceFinalQuota;
            _replayAttemptQuota = replayAttemptQuota;
        }

        public int SemanticFinalQuota => Math.Min(
            _semanticFinalQuota,
            _parent?.SemanticFinalQuota ?? int.MaxValue);

        public int MaterializedOccurrenceFinalQuota => Math.Min(
            _materializedOccurrenceFinalQuota,
            _parent?.MaterializedOccurrenceFinalQuota ?? int.MaxValue);

        public int ReplayAttemptQuota => Math.Min(
            _replayAttemptQuota,
            _parent?.ReplayAttemptQuota ?? int.MaxValue);

        public int ActiveFinalQuota => SaturatingQuotaSum(
            SemanticFinalQuota,
            MaterializedOccurrenceFinalQuota);

        public bool HasWork => ActiveFinalQuota > 0 && ReplayAttemptQuota > 0;

        public ChoiceSearchBudget CreateChildLease(
            int activeFinalQuota,
            int replayAttemptQuota)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(activeFinalQuota);
            ArgumentOutOfRangeException.ThrowIfNegative(replayAttemptQuota);
            int semanticFinalQuota = Math.Min(SemanticFinalQuota, activeFinalQuota);
            int occurrenceFinalQuota = Math.Min(
                MaterializedOccurrenceFinalQuota,
                activeFinalQuota - semanticFinalQuota);
            return new ChoiceSearchBudget(
                this,
                semanticFinalQuota,
                occurrenceFinalQuota,
                Math.Min(ReplayAttemptQuota, replayAttemptQuota));
        }

        public bool TrySpendReplayAttempt()
        {
            if (_replayAttemptQuota <= 0)
                return false;
            if (_parent != null && !_parent.TrySpendReplayAttempt())
                return false;
            _replayAttemptQuota--;
            return true;
        }

        public bool TryConsumeFinal()
        {
            if (SemanticFinalQuota > 0 && TryConsumeSemanticFinal())
                return true;
            return MaterializedOccurrenceFinalQuota > 0
                && TryConsumeOccurrenceFinal();
        }

        private bool TryConsumeSemanticFinal()
        {
            if (_semanticFinalQuota <= 0)
                return false;
            if (_parent != null && !_parent.TryConsumeSemanticFinal())
                return false;
            _semanticFinalQuota--;
            return true;
        }

        private bool TryConsumeOccurrenceFinal()
        {
            if (_materializedOccurrenceFinalQuota <= 0)
                return false;
            if (_parent != null && !_parent.TryConsumeOccurrenceFinal())
                return false;
            _materializedOccurrenceFinalQuota--;
            return true;
        }

        private static int SaturatingQuotaSum(int left, int right)
            => left > int.MaxValue - right ? int.MaxValue : left + right;
    }

    private readonly record struct WholeActionChoiceBudget(
        ChoiceSearchBudget SemanticSearchBudget,
        int OccurrenceFinalReserve,
        int OccurrenceReplayAttemptQuota);

    private readonly record struct DeferredOccurrenceChoiceBranch(
        PendingChoiceReplayBranch Branch,
        PrimaryChoiceMatch? UnresolvedPrimaryChoice);

    /// <summary>
    /// Collects physical-identity supplements across the whole action. Non-identity layers share
    /// this coordinator instead of pre-assigning its two slots to outer branches: a later stable
    /// semantic branch can therefore still expose the first actual identity-sensitive frontier.
    /// Supplements are replayed only after the semantic pass, so they can never replace a distinct
    /// semantic decision. The collector is action-local and has one ordered consumer; workers never race
    /// on a shared counter.
    /// </summary>
    private sealed class ChoiceOccurrenceCollector<TCandidate>(
        int finalReserve,
        int replayAttemptQuota)
    {
        private readonly List<TCandidate> _branches = [];
        private bool _sealed;

        public int FinalReserve { get; } = finalReserve is >= 0 and
            <= CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches
                ? finalReserve
                : throw new ArgumentOutOfRangeException(nameof(finalReserve));

        public int ReplayAttemptQuota { get; } = replayAttemptQuota >= 0
            ? replayAttemptQuota
            : throw new ArgumentOutOfRangeException(nameof(replayAttemptQuota));

        public int RemainingFinalReserve => _sealed
            ? 0
            : Math.Max(0, FinalReserve - _branches.Count);

        public IReadOnlyList<TCandidate> Seal()
        {
            _sealed = true;
            return _branches;
        }

        public void Collect(TCandidate candidate)
        {
            if (_sealed
                || RemainingFinalReserve <= 0
                || ReplayAttemptQuota <= _branches.Count)
            {
                return;
            }
            _branches.Add(candidate);
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePrimaryCardChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot probeSnapshot,
        CardChoiceSpec? choiceSpec,
        PlanCardChoice? requiredEmptyChoice)
    {
        PrimaryCardChoiceLayer layer = BuildPrimaryCardChoiceLayer(
            action,
            probeSnapshot,
            choiceSpec,
            requiredEmptyChoice);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolvePrimaryCardChoiceLayer(
                     node,
                     action,
                     probeSnapshot,
                     layer))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

    private PrimaryCardChoiceLayer BuildPrimaryCardChoiceLayer(
        PlanAction action,
        SimulationSnapshot probeSnapshot,
        CardChoiceSpec? choiceSpec,
        PlanCardChoice? requiredEmptyChoice)
    {
        IReadOnlyList<PlanCardChoice?> choices;
        if (choiceSpec == null)
        {
            choices = requiredEmptyChoice == null ? [null] : [requiredEmptyChoice];
        }
        else
        {
            IReadOnlyList<PlanCardChoice> builtChoices = CardChoiceSupport.BuildChoices(
                choiceSpec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
            if (action.ReplayCount > 0 && builtChoices.Count > 1)
            {
                int choiceEvents = checked(action.ReplayCount + 1);
                int configuredWholeActionBranchLimit =
                    ResolveConfiguredWholeActionChoiceBranchLimit(choiceSpec);
                int initialSemanticChoiceLimit = Math.Max(
                    1,
                    (int)Math.Ceiling(Math.Pow(
                        configuredWholeActionBranchLimit,
                        1d / choiceEvents)));
                builtChoices = CardChoiceSupport.TakeChoicesWithIdentityOccurrenceReserve(
                    builtChoices,
                    choiceSpec.Effect,
                    initialSemanticChoiceLimit);
            }
            choices = builtChoices.Cast<PlanCardChoice?>().ToList();
        }
        if (choices.Count == 0)
            choices = [null];
        bool identityChangingLayer = choiceSpec != null
            && CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(choiceSpec.Effect);
        int semanticBranchCount = identityChangingLayer
            ? CardChoiceSupport.CountSemanticChoices(
                choices.Where(choice => choice != null).Cast<PlanCardChoice>().ToList())
            : choices.Count;
        WholeActionChoiceBudget wholeActionBudget = CreateWholeActionChoiceBudget(
            choiceSpec,
            semanticBranchCount);

        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        bool unregisteredPendingChoice = probeSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
            && probeCombat.PendingTurnStartChoice == null
            && probeCombat.PendingKnowledgeDemonChoice == null;
        return new PrimaryCardChoiceLayer(
            choices,
            unregisteredPendingChoice,
            semanticBranchCount,
            identityChangingLayer,
            wholeActionBudget);
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePrimaryCardChoiceLayer(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot? probeSnapshot,
        PrimaryCardChoiceLayer layer,
        PrimaryChoiceReplayFrontier? replayedChoices = null)
    {
        bool retainsProbeSnapshot = layer.Choices.Contains(null);
        if (!retainsProbeSnapshot)
            probeSnapshot?.ReleaseSimulator();
        if (layer.UnregisteredPendingChoice)
        {
            if (retainsProbeSnapshot)
                probeSnapshot?.ReleaseSimulator();
            throw new InvalidOperationException(
                $"卡牌 {action.CardId} 产生了未登记的分支选择，不能静默回退到原生重扫。");
        }
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector = new(
            layer.WholeActionBudget.OccurrenceFinalReserve,
            layer.WholeActionBudget.OccurrenceReplayAttemptQuota);
        if (layer.IdentityChangingLayer)
        {
            for (int choiceIndex = layer.SemanticBranchCount;
                 choiceIndex < layer.Choices.Count;
                 choiceIndex++)
            {
                PlanCardChoice choice = layer.Choices[choiceIndex]
                    ?? throw new InvalidOperationException(
                        "身份敏感 primary choice 的 occurrence 分支不能为空。");
                occurrenceCollector.Collect(new DeferredOccurrenceChoiceBranch(
                    new PendingChoiceReplayBranch(
                        action with { Choice = choice },
                        PruneInvalidBranch: true),
                    UnresolvedPrimaryChoice: null));
            }
        }

        for (int choiceIndex = 0;
             choiceIndex < layer.SemanticBranchCount;
             choiceIndex++)
        {
            ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                layer.WholeActionBudget.SemanticSearchBudget,
                layer.SemanticBranchCount - choiceIndex - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(
                    layer.SemanticBranchCount - choiceIndex);
                break;
            }
            PlanCardChoice? choice = layer.Choices[choiceIndex];
            PlanAction resolvedAction = replayedChoices?.Actions[choiceIndex]
                ?? action with { Choice = choice };
            SimulationSnapshot childSnapshot;
            if (choice == null)
            {
                childSnapshot = probeSnapshot
                    ?? throw new InvalidOperationException("无选牌动作缺少 probe 快照。");
            }
            else
            {
                if (replayedChoices == null && !TrySpendChoiceReplayAttempt(branchBudget))
                    yield break;
                SimulationSnapshot? replayedChoice = replayedChoices == null
                    ? ReplayPlannedChoiceBranch(node, resolvedAction)
                    : replayedChoices.Take(choiceIndex, branchBudget);
                if (replayedChoice == null)
                    continue;
                childSnapshot = replayedChoice;
            }
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         resolvedAction,
                         childSnapshot,
                         unresolvedPrimaryChoice: null,
                         searchBudget: branchBudget,
                         occurrenceCollector: occurrenceCollector))
            {
                yield return (finalAction, finalSnapshot);
            }
        }

        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolveCollectedOccurrenceChoiceBranches(node, occurrenceCollector))
        {
            yield return (finalAction, finalSnapshot);
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        ResolveExplicitCardChoiceBranches(
            SearchNode node,
            PlanAction baseAction,
            SimulationSnapshot? probeSnapshot,
            IReadOnlyList<PlanCardChoice?> choices,
            CardChoiceSpec? choiceSpec,
            PrimaryChoiceReplayFrontier? replayedChoices = null)
    {
        bool identityChangingLayer = replayedChoices?.Layer.IdentityChangingLayer
            ?? (choiceSpec != null
                && CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(choiceSpec.Effect));
        int semanticBranchCount = replayedChoices?.Layer.SemanticBranchCount ?? (identityChangingLayer
            ? CardChoiceSupport.CountSemanticChoices(
                choices.Where(choice => choice != null).Cast<PlanCardChoice>().ToList())
            : choices.Count);
        WholeActionChoiceBudget wholeActionBudget = replayedChoices?.Layer.WholeActionBudget
            ?? CreateWholeActionChoiceBudget(
            choiceSpec,
            semanticBranchCount);
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector = new(
            wholeActionBudget.OccurrenceFinalReserve,
            wholeActionBudget.OccurrenceReplayAttemptQuota);

        if (identityChangingLayer)
        {
            for (int index = semanticBranchCount; index < choices.Count; index++)
            {
                PlanCardChoice choice = choices[index]
                    ?? throw new InvalidOperationException(
                        "身份敏感显式选牌的 occurrence 分支不能为空。");
                occurrenceCollector.Collect(new DeferredOccurrenceChoiceBranch(
                    new PendingChoiceReplayBranch(
                        baseAction with { Choice = choice },
                        PruneInvalidBranch: true),
                    UnresolvedPrimaryChoice: null));
            }
        }

        for (int index = 0; index < semanticBranchCount; index++)
        {
            ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                wholeActionBudget.SemanticSearchBudget,
                semanticBranchCount - index - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(semanticBranchCount - index);
                break;
            }
            PlanCardChoice? choice = choices[index];
            PlanAction action = replayedChoices?.Actions[index] ?? baseAction with { Choice = choice };
            SimulationSnapshot childSnapshot;
            if (choice == null)
            {
                childSnapshot = probeSnapshot
                    ?? throw new InvalidOperationException("无选牌动作缺少 probe 快照。");
            }
            else
            {
                if (replayedChoices == null && !TrySpendChoiceReplayAttempt(branchBudget))
                    continue;
                SimulationSnapshot? replayedChoice = replayedChoices == null
                    ? ReplayPlannedChoiceBranch(node, action)
                    : replayedChoices.Take(index, branchBudget);
                if (replayedChoice == null)
                    continue;
                childSnapshot = replayedChoice;
            }
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         action,
                         childSnapshot,
                         unresolvedPrimaryChoice: null,
                         searchBudget: branchBudget,
                         occurrenceCollector: occurrenceCollector))
            {
                yield return (finalAction, finalSnapshot);
            }
        }

        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolveCollectedOccurrenceChoiceBranches(node, occurrenceCollector))
        {
            yield return (finalAction, finalSnapshot);
        }
    }

    private int ResolveConfiguredWholeActionChoiceBranchLimit(CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec?.SourcePile == PileType.Hand
            ? _profile.MaxHandChoiceBranchesPerAction
            : primaryChoiceSpec == null
                ? Math.Max(
                    _profile.MaxPileChoiceBranchesPerAction,
                    _profile.MaxHandChoiceBranchesPerAction)
                : _profile.MaxPileChoiceBranchesPerAction;

    private WholeActionChoiceBudget CreateWholeActionChoiceBudget(
        CardChoiceSpec? primaryChoiceSpec,
        int minimumSemanticFinalQuota = 1,
        PendingChoiceBudgetSeed? currentPendingChoice = null)
    {
        int configuredFinalQuota = ResolveConfiguredWholeActionChoiceBranchLimit(
            primaryChoiceSpec);
        if (currentPendingChoice != null)
        {
            configuredFinalQuota = Math.Max(
                configuredFinalQuota,
                ResolveConfiguredWholeActionChoiceBranchLimit(currentPendingChoice.Spec));
        }
        return ResolveSeededWholeActionChoiceBudgetCore(
            configuredFinalQuota,
            minimumSemanticFinalQuota,
            currentPendingChoice?.SemanticBranchCount ?? 0);
    }

    private static WholeActionChoiceBudget ResolveSeededWholeActionChoiceBudgetCore(
        int configuredFinalQuota,
        int primarySemanticBranchCount,
        int pendingSemanticBranchCount)
    {
        if (configuredFinalQuota < 1)
            throw new ArgumentOutOfRangeException(nameof(configuredFinalQuota));
        if (primarySemanticBranchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(primarySemanticBranchCount));
        if (pendingSemanticBranchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pendingSemanticBranchCount));
        return ResolveWholeActionChoiceBudgetCore(Math.Max(
            configuredFinalQuota,
            Math.Max(primarySemanticBranchCount, pendingSemanticBranchCount)));
    }

    private static WholeActionChoiceBudget ResolveWholeActionChoiceBudgetCore(
        int semanticFinalQuota)
    {
        if (semanticFinalQuota < 1)
            throw new ArgumentOutOfRangeException(nameof(semanticFinalQuota));
        int maximumFinalQuota = semanticFinalQuota
            > int.MaxValue - CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches
                ? int.MaxValue
                : semanticFinalQuota
                    + CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches;
        int totalReplayAttemptQuota = ResolveFiniteChoiceReplayAttemptLimit(maximumFinalQuota);

        // Semantic decisions always receive the capped attempt budget first. Only genuine cap
        // headroom is reserved for the later physical-occurrence pass, so a 600-way semantic
        // layer under the 512 hard cap keeps 512 different decisions and zero supplements.
        int minimumSemanticAttempts = Math.Min(
            semanticFinalQuota,
            totalReplayAttemptQuota);
        int occurrenceReplayAttemptQuota = Math.Min(
            CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches * 4,
            totalReplayAttemptQuota - minimumSemanticAttempts);
        int occurrenceFinalReserve = Math.Min(
            CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches,
            occurrenceReplayAttemptQuota);
        return new WholeActionChoiceBudget(
            new ChoiceSearchBudget(
                semanticFinalQuota,
                materializedOccurrenceFinalQuota: 0,
                totalReplayAttemptQuota - occurrenceReplayAttemptQuota),
            occurrenceFinalReserve,
            occurrenceReplayAttemptQuota);
    }

    private static ChoiceSearchBudget? CreateChoiceBranchBudgetCore(
        ChoiceSearchBudget parentBudget,
        int laterSiblingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(laterSiblingCount);
        int effectiveFinalQuota = Math.Min(
            parentBudget.ActiveFinalQuota,
            parentBudget.ReplayAttemptQuota);
        if (effectiveFinalQuota <= 0)
            return null;

        // Divide the live remainder fairly among this branch and its later stable siblings. This
        // reproduces the old 4/3/3 breadth when every branch uses its share, but a shallow or
        // invalid branch leaves its unspent quota in the parent; the next branch then receives a
        // larger share of the new remainder instead of losing that work permanently.
        int remainingSiblingCount = checked(laterSiblingCount + 1);
        int finalQuota = DivideRoundUp(effectiveFinalQuota, remainingSiblingCount);
        int attemptQuota = DivideRoundUp(
            parentBudget.ReplayAttemptQuota,
            remainingSiblingCount);
        return parentBudget.CreateChildLease(
            finalQuota,
            attemptQuota);
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (divisor < 1)
            throw new ArgumentOutOfRangeException(nameof(divisor));
        return value == 0 ? 0 : 1 + (value - 1) / divisor;
    }

    private static int ResolveFiniteChoiceReplayAttemptLimit(int finiteFinalBranches)
    {
        // Four replay attempts per retained leaf covers ordinary primary + nested chains while
        // keeping a hard ceiling for recursive generators. Custom profiles remain bounded too.
        return (int)Math.Min(512L, Math.Max(1L, finiteFinalBranches * 4L));
    }

    internal static void VerifyChoiceReplayBranchBudgetPolicyForTesting()
    {
        VerifyPrimaryReplayReservation();
        if (ResolveFiniteChoiceReplayAttemptLimit(200) != 512)
            throw new InvalidOperationException("选择 replay 工作预算没有保持 512 次硬上限。");

        // A shallow first branch must return all unused attempts to a later deep branch. The old
        // static 4/4 split failed this exact 1+5 replay distribution despite spending only 5/8.
        ChoiceSearchBudget skewed = new(2, 0, 8);
        ChoiceSearchBudget first = CreateChoiceBranchBudgetCore(skewed, laterSiblingCount: 1)
            ?? throw new InvalidOperationException("倾斜预算没有创建首分支 lease。");
        if (!first.TrySpendReplayAttempt() || !first.TryConsumeFinal())
            throw new InvalidOperationException("倾斜预算首分支无法提交一个浅层结果。");
        ChoiceSearchBudget second = CreateChoiceBranchBudgetCore(skewed, laterSiblingCount: 0)
            ?? throw new InvalidOperationException("倾斜预算没有回收额度给后置分支。");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!second.TrySpendReplayAttempt())
                throw new InvalidOperationException("后置深分支没有拿到前置分支未使用的 replay 额度。");
        }
        if (!second.TryConsumeFinal()
            || skewed.ActiveFinalQuota != 0
            || skewed.ReplayAttemptQuota != 2)
        {
            throw new InvalidOperationException("倾斜预算回收后的实际扣费不正确。");
        }

        // Invalid prefixes consume replay work but not final capacity; the next stable branch may
        // use both unclaimed leaves.
        ChoiceSearchBudget invalidPrefix = new(2, 0, 8);
        ChoiceSearchBudget invalidFirst = CreateChoiceBranchBudgetCore(
            invalidPrefix,
            laterSiblingCount: 1)!;
        if (!invalidFirst.TrySpendReplayAttempt())
            throw new InvalidOperationException("无效前缀无法记录 replay 消耗。");
        ChoiceSearchBudget recovered = CreateChoiceBranchBudgetCore(
            invalidPrefix,
            laterSiblingCount: 0)!;
        if (recovered.ActiveFinalQuota != 2 || recovered.ReplayAttemptQuota != 7)
            throw new InvalidOperationException("无效前缀错误吞掉了未产出的 final 配额。");

        // With no returned work, live sharing is exactly the former stable 4/3/3 allocation.
        ChoiceSearchBudget breadth = new(10, 0, 40);
        int[] expectedFinals = [4, 3, 3];
        for (int index = 0; index < 3; index++)
        {
            ChoiceSearchBudget branch = CreateChoiceBranchBudgetCore(
                breadth,
                laterSiblingCount: 2 - index)!;
            for (int consumed = 0; consumed < expectedFinals[index]; consumed++)
            {
                if (!branch.TrySpendReplayAttempt() || !branch.TryConsumeFinal())
                    throw new InvalidOperationException("稳定 sibling reserve 被提前分支突破。");
            }
        }
        if (breadth.ActiveFinalQuota != 0 || breadth.ReplayAttemptQuota != 30)
            throw new InvalidOperationException("稳定 sibling reserve 的总量扣费不正确。");

        // A 600-way layer performs exactly 512 real replays and leaves an observable 88-branch
        // tail for the caller to report as budget-limited.
        ChoiceSearchBudget capped = new(600, 0, 512);
        int scheduled = 0;
        for (int index = 0; index < 600; index++)
        {
            ChoiceSearchBudget? branch = CreateChoiceBranchBudgetCore(
                capped,
                laterSiblingCount: 599 - index);
            if (branch == null)
                break;
            if (!branch.TrySpendReplayAttempt() || !branch.TryConsumeFinal())
                throw new InvalidOperationException("超宽 choice layer 的实际 replay 扣费失败。");
            scheduled++;
        }
        if (scheduled != 512
            || capped.ActiveFinalQuota != 88
            || capped.ReplayAttemptQuota != 0)
        {
            throw new InvalidOperationException(
                "超宽 choice layer 没有形成 512 replay / 88 unresolved 的显式截断。");
        }

        WholeActionChoiceBudget widePendingEndTurn =
            ResolveSeededWholeActionChoiceBudgetCore(
                configuredFinalQuota: 3,
                primarySemanticBranchCount: 1,
                pendingSemanticBranchCount: 10);
        if (widePendingEndTurn.SemanticSearchBudget.SemanticFinalQuota != 10)
            throw new InvalidOperationException("EndTurn/setup 的 exact 首层宽度被 profile 提前截断。");

        WholeActionChoiceBudget widePrimaryAfterPending =
            ResolveSeededWholeActionChoiceBudgetCore(
                configuredFinalQuota: 3,
                primarySemanticBranchCount: 10,
                pendingSemanticBranchCount: 2);
        if (widePrimaryAfterPending.SemanticSearchBudget.SemanticFinalQuota != 10)
            throw new InvalidOperationException("choice-before-primary 丢失已知下游语义宽度。");

        WholeActionChoiceBudget oversizedPending =
            ResolveSeededWholeActionChoiceBudgetCore(
                configuredFinalQuota: 3,
                primarySemanticBranchCount: 1,
                pendingSemanticBranchCount: 600);
        if (oversizedPending.OccurrenceFinalReserve != 0
            || oversizedPending.OccurrenceReplayAttemptQuota != 0
            || oversizedPending.SemanticSearchBudget.SemanticFinalQuota != 600
            || oversizedPending.SemanticSearchBudget.ReplayAttemptQuota != 512)
        {
            throw new InvalidOperationException("超宽 pending 的 whole-action 上限或优先级错误。");
        }

        WholeActionChoiceBudget nestedWholeAction =
            ResolveWholeActionChoiceBudgetCore(semanticFinalQuota: 3);
        ChoiceOccurrenceCollector<int> nestedCollector = new(
            nestedWholeAction.OccurrenceFinalReserve,
            nestedWholeAction.OccurrenceReplayAttemptQuota);
        // A/B and the next non-identity layer merely carry the action-local collector. Only the
        // later C frontier registers physical branches, so fixed outer-token preallocation cannot
        // accidentally starve it.
        if (nestedCollector.RemainingFinalReserve != 2)
            throw new InvalidOperationException("非身份层提前消耗了 occurrence reserve。");
        nestedCollector.Collect(1);
        nestedCollector.Collect(2);
        IReadOnlyList<int> nestedBranches = nestedCollector.Seal();
        nestedCollector.Collect(3);
        if (nestedBranches.Count != 2
            || !nestedBranches.SequenceEqual([1, 2])
            || nestedCollector.RemainingFinalReserve != 0
            || nestedCollector.ReplayAttemptQuota <= 0)
        {
            throw new InvalidOperationException(
                "nested identity 的全 action reserve 被预分丢失、跨层累加或突破 attempt 上限。");
        }

        foreach (int semanticQuota in new[] { 1, 2, 3, 127, 510, 511, 512, 600 })
        {
            WholeActionChoiceBudget wholeAction =
                ResolveWholeActionChoiceBudgetCore(semanticQuota);
            if (wholeAction.SemanticSearchBudget.ReplayAttemptQuota
                    + wholeAction.OccurrenceReplayAttemptQuota > 512
                || Math.Min(
                        wholeAction.SemanticSearchBudget.SemanticFinalQuota,
                        wholeAction.SemanticSearchBudget.ReplayAttemptQuota)
                    + wholeAction.OccurrenceFinalReserve > 512
                || wholeAction.OccurrenceFinalReserve
                    > CardChoiceSupport.MaximumIdentityOccurrenceReservedBranches)
            {
                throw new InvalidOperationException(
                    $"whole-chain choice 预算越界：semantic={semanticQuota}，" +
                    $"final={wholeAction.SemanticSearchBudget.SemanticFinalQuota}" +
                    $"+{wholeAction.OccurrenceFinalReserve}，" +
                    $"attempt={wholeAction.SemanticSearchBudget.ReplayAttemptQuota}" +
                    $"+{wholeAction.OccurrenceReplayAttemptQuota}。");
            }
        }
    }

    private static void VerifyPrimaryReplayReservation()
    {
        if (CanReservePrimaryReplayPrefix(600, 600, 512)
            || CanReservePrimaryReplayPrefix(2, 1, 8)
            || CanReservePrimaryReplayPrefix(1, 1, 8))
        {
            throw new InvalidOperationException("首层并行回放没有保留不足配额/单分支的串行边界。");
        }
        foreach (int width in new[] { 2, 3, 7, 31, 127, 511, 512 })
        foreach (int finals in new[] { width, width + 1, width * 2 })
        foreach (int attempts in new[] { width, Math.Min(512, width + 3), 512 })
        foreach (int pattern in new[] { 0, 1, 2 })
        {
            if (!CanReservePrimaryReplayPrefix(width, finals, attempts))
                throw new InvalidOperationException("可保证准入的首层回放被错误拒绝。");
            ChoiceSearchBudget budget = new(finals, 0, attempts);
            for (int index = 0; index < width; index++)
            {
                ChoiceSearchBudget? child = CreateChoiceBranchBudgetCore(budget, width - index - 1);
                if (child == null || !child.TrySpendReplayAttempt())
                    throw new InvalidOperationException("前置分支消费额度后，后置首层回放失去了准入保证。");
                // Fully exhausted nested chains, invalid prefixes, and alternating shallow/deep
                // siblings cover both independent maxima and actual unused-quota return.
                if (pattern == 0 || pattern == 2 && index % 2 == 0)
                {
                    while (child.TrySpendReplayAttempt()) { }
                    while (child.TryConsumeFinal()) { }
                }
                else if (pattern == 2)
                {
                    if (!child.TryConsumeFinal())
                        throw new InvalidOperationException("首层浅分支没有获得最终结果额度。");
                }
            }
        }
    }

    private bool TrySpendChoiceReplayAttempt(ChoiceSearchBudget budget)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _run.WorkPacer.YieldIfNeeded();
        if (budget.TrySpendReplayAttempt())
        {
            _run.ChoiceReplayAttempts++;
            _run.ChoiceBranchesEvaluated++;
            return true;
        }
        _run.ChoiceReplayBudgetExhaustions++;
        return false;
    }

    private void RecordChoiceBranchesDroppedByBudget(int count)
    {
        if (count <= 0)
            return;
        _run.ChoiceBranchesDroppedByBudget += count;
        _run.ChoiceReplayBudgetExhaustions++;
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        ResolveCollectedOccurrenceChoiceBranches(
            SearchNode node,
            ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> collector)
    {
        IReadOnlyList<DeferredOccurrenceChoiceBranch> branches = collector.Seal();
        if (branches.Count == 0)
            yield break;

        ChoiceSearchBudget occurrenceBudget = new(
            semanticFinalQuota: 0,
            materializedOccurrenceFinalQuota: collector.FinalReserve,
            replayAttemptQuota: collector.ReplayAttemptQuota);
        for (int index = 0; index < branches.Count; index++)
        {
            DeferredOccurrenceChoiceBranch candidate = branches[index];
            ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                occurrenceBudget,
                branches.Count - index - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(branches.Count - index);
                break;
            }
            if (!TrySpendChoiceReplayAttempt(branchBudget))
                break;
            SimulationSnapshot? branchSnapshot = ReplayPendingChoiceBranch(
                node,
                candidate.Branch);
            if (branchSnapshot == null)
                continue;
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         candidate.Branch.Action,
                         branchSnapshot,
                         candidate.UnresolvedPrimaryChoice,
                         branchBudget,
                         collector))
            {
                yield return (finalAction, finalSnapshot);
            }
        }
    }

    private CardChoiceSpec? BuildPrimaryCardChoiceSpec(SimulationSnapshot probeSnapshot)
    {
        CombatPredictionSimulator probeSimulator =
            (CombatPredictionSimulator)probeSnapshot.Simulator;
        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        return probeCombat.PendingTurnStartChoice is { } pendingChoice
            && string.IsNullOrEmpty(pendingChoice.SourceId)
                ? TurnStartChoiceSupport.BuildPendingSpec(
                    probeSimulator,
                    probeCombat,
                    _player)
                : null;
    }

    /// <summary>
    /// Inspects the pending boundary without advancing or mutating its simulator. Root-level
    /// choice coordinators use the actual first-layer semantic width instead of silently falling
    /// back to the configured profile width. Setup can also reuse the materialized turn-start
    /// layer so the potentially wide choice list is not built twice.
    /// </summary>
    private PendingChoiceBudgetSeed? BuildCurrentPendingChoiceBudgetSeed(
        SimulationSnapshot snapshot)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
            return null;

        SimulatedCombatState combat =
            (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> knowledgeBranches =
                KnowledgeDemonChoiceSupport.BuildChoices(knowledgeRequest, displayNames);
            return new PendingChoiceBudgetSeed(
                Spec: null,
                SemanticBranchCount: knowledgeBranches.Count,
                TurnSetupLayer: null);
        }

        if (combat.PendingTurnStartChoice is not { } request)
            return null;

        CombatPredictionSimulator simulator =
            (CombatPredictionSimulator)snapshot.Simulator;
        CardChoiceSpec spec =
            TurnStartChoiceSupport.BuildPendingSpec(simulator, combat, _player);
        IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
            spec,
            displayNames,
            _profile.MaxPileChoiceBranchesPerAction,
            _profile.MaxHandChoiceBranchesPerAction);
        int semanticBranchCount =
            CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect)
                ? CardChoiceSupport.CountSemanticChoices(branches)
                : branches.Count;
        return new PendingChoiceBudgetSeed(
            spec,
            semanticBranchCount,
            new TurnSetupChoiceLayer(request, spec, branches));
    }

    private int BuildChoiceSpecSemanticBranchCount(CardChoiceSpec? spec)
    {
        if (spec == null)
            return 1;
        IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
            spec,
            displayNames,
            _profile.MaxPileChoiceBranchesPerAction,
            _profile.MaxHandChoiceBranchesPerAction);
        return CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect)
            ? CardChoiceSupport.CountSemanticChoices(branches)
            : branches.Count;
    }

    private static CardChoiceSpec? BuildRequiredEmptyChoiceSpec(PlanCardChoice? requiredEmptyChoice)
        => requiredEmptyChoice == null
            ? null
            : new CardChoiceSpec(
                requiredEmptyChoice.Effect,
                requiredEmptyChoice.SourcePile,
                0,
                0,
                [],
                [],
                ReplacementValue: 0d);

    private static bool HasChoiceBeforePrimary(
        SimulationSnapshot snapshot,
        CardChoiceSpec? primaryChoiceSpec)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
            return false;
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingTurnStartChoice is not { } request)
            return combat.PendingKnowledgeDemonChoice != null;
        return !MatchesPrimaryChoice(request, primaryChoiceSpec);
    }

    private static bool MatchesPrimaryChoice(
        TurnStartChoiceRequest request,
        CardChoiceSpec? primaryChoiceSpec)
        => MatchesPrimaryChoice(request, BuildPrimaryChoiceMatch(primaryChoiceSpec));

    private static PrimaryChoiceMatch? BuildPrimaryChoiceMatch(
        CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec == null
            ? null
            : new PrimaryChoiceMatch(
                primaryChoiceSpec.ContextId,
                primaryChoiceSpec.Effect,
                primaryChoiceSpec.SourcePile,
                primaryChoiceSpec.MinCount);

    private static bool MatchesPrimaryChoice(
        TurnStartChoiceRequest request,
        PrimaryChoiceMatch? primaryChoice)
        => primaryChoice is { } match
            && string.IsNullOrEmpty(request.SourceId)
            && string.Equals(request.ContextId, match.ContextId, StringComparison.Ordinal)
            && request.Effect == match.Effect
            && request.SourcePile == match.SourcePile
            && request.Count == match.MinCount;

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolveRoundChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        PrimaryChoiceMatch? unresolvedPrimaryChoice = null,
        WholeActionChoiceBudget? wholeActionBudget = null,
        CardChoiceSpec? budgetPrimaryChoiceSpec = null)
    {
        WholeActionChoiceBudget budget;
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector;
        try
        {
            budget = wholeActionBudget
                ?? CreateWholeActionChoiceBudget(
                    budgetPrimaryChoiceSpec,
                    BuildChoiceSpecSemanticBranchCount(budgetPrimaryChoiceSpec),
                    BuildCurrentPendingChoiceBudgetSeed(snapshot));
            occurrenceCollector = new ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch>(
                budget.OccurrenceFinalReserve,
                budget.OccurrenceReplayAttemptQuota);
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolveRoundChoiceBranches(
                     node,
                     action,
                     snapshot,
                     unresolvedPrimaryChoice,
                     budget.SemanticSearchBudget,
                     occurrenceCollector))
        {
            yield return (finalAction, finalSnapshot);
        }
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolveCollectedOccurrenceChoiceBranches(node, occurrenceCollector))
        {
            yield return (finalAction, finalSnapshot);
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolveRoundChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        PrimaryChoiceMatch? unresolvedPrimaryChoice,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector)
    {
        if (!GrowthCostPolicy.AllowsBrightestFlame(policy.BrightestFlameMaxHpLossLimit,
                root.InitialBrightestFlameMaxHpSpent, snapshot.BrightestFlameMaxHpSpent))
        {
            snapshot.ReleaseSimulator();
            yield break;
        }
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            if (searchBudget.TryConsumeFinal())
                yield return (action, snapshot);
            else
            {
                RecordChoiceBranchesDroppedByBudget(1);
                snapshot.ReleaseSimulator();
            }
            yield break;
        }

        if (!searchBudget.HasWork)
        {
            _run.ChoiceReplayBudgetExhaustions++;
            snapshot.ReleaseSimulator();
            yield break;
        }
        PendingChoiceReplayLayer layer;
        try
        {
            layer = BuildPendingChoiceReplayLayer(
                node,
                action,
                snapshot,
                unresolvedPrimaryChoice,
                searchBudget,
                occurrenceCollector);
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
        snapshot.ReleaseSimulator();
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolvePendingChoiceReplayLayer(
                     node,
                     layer,
                     unresolvedPrimaryChoice,
                     searchBudget,
                     occurrenceCollector))
        {
            yield return (finalAction, finalSnapshot);
        }
    }

    private PendingChoiceReplayLayer BuildPendingChoiceReplayLayer(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        PrimaryChoiceMatch? unresolvedPrimaryChoice,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> branches = KnowledgeDemonChoiceSupport.BuildChoices(
                knowledgeRequest,
                displayNames);
            List<PendingChoiceReplayBranch> resolvedBranches = new(branches.Count);
            foreach (PlanCardChoice branch in branches)
            {
                IReadOnlyList<PlanCardChoice> existing = action.TurnStartChoices ?? [];
                List<PlanCardChoice> next = new(existing.Count + 1);
                next.AddRange(existing);
                next.Add(branch);
                PlanAction resolvedAction = action with
                {
                    TurnStartChoices = next,
                };
                resolvedBranches.Add(new PendingChoiceReplayBranch(
                    resolvedAction,
                    PruneInvalidBranch: true));
            }
            return new PendingChoiceReplayLayer(resolvedBranches);
        }

        if (combat.PendingTurnStartChoice is { } request)
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(simulator, combat, _player);
            IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
                spec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
            bool identityChangingLayer =
                CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect);
            branches = CardChoiceSupport.TakeChoicesWithIdentityOccurrenceReserve(
                branches,
                spec.Effect,
                Math.Max(1, searchBudget.ActiveFinalQuota),
                identityChangingLayer
                    ? occurrenceCollector.RemainingFinalReserve
                    : 0);
            int semanticBranchCount = identityChangingLayer
                ? CardChoiceSupport.CountSemanticChoices(branches)
                : branches.Count;
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || snapshot.Turn > node.Turn
                // A forced end can suspend before AdvancePlayerTurn. Its choice still
                // belongs to the round transition, not the card's own choice sequence.
                || request.Timing is PlanChoiceTiming.PlayerTurnEnd
                    or PlanChoiceTiming.EnemyTurn or PlanChoiceTiming.PlayerTurnStart;
            bool primaryChoice = !turnResolution
                && action.Choice == null
                && string.IsNullOrEmpty(request.SourceId)
                && (unresolvedPrimaryChoice == null
                    || MatchesPrimaryChoice(request, unresolvedPrimaryChoice));
            IReadOnlyList<PlanCardChoice> existing = turnResolution
                ? action.TurnStartChoices ?? []
                : action.NestedChoices ?? [];
            List<PendingChoiceReplayBranch> resolvedBranches = new(branches.Count);
            foreach (PlanCardChoice branch in branches)
            {
                List<PlanCardChoice> next = new(existing.Count + 1);
                next.AddRange(existing);
                PlanCardChoice resolvedBranch = branch with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                    Timing = request.Timing,
                };
                if (!primaryChoice)
                    next.Add(resolvedBranch);
                PlanAction resolvedAction = turnResolution
                    ? action with { TurnStartChoices = next }
                    : primaryChoice
                        ? action with { Choice = resolvedBranch }
                        : action with
                        {
                            NestedChoices = next,
                            NestedChoicesBeforePrimary = action.Choice == null
                                ? action.NestedChoicesBeforePrimary + 1
                                : action.NestedChoicesBeforePrimary,
                        };
                resolvedBranches.Add(new PendingChoiceReplayBranch(
                    resolvedAction,
                    PruneInvalidBranch: true));
            }
            if (identityChangingLayer)
            {
                for (int index = semanticBranchCount; index < resolvedBranches.Count; index++)
                {
                    occurrenceCollector.Collect(new DeferredOccurrenceChoiceBranch(
                        resolvedBranches[index],
                        unresolvedPrimaryChoice));
                }
            }
            return new PendingChoiceReplayLayer(
                resolvedBranches.Take(semanticBranchCount).ToList(), TakeExecutionChoiceCheckpoint(node, action, snapshot));
        }
        throw new InvalidOperationException(
            $"动作 {PolicyActionToken(action)} 产生了未登记的分支选择，不能留下等待原生结算的搜索边界。");
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        ResolvePendingChoiceReplayLayer(
        SearchNode node,
        PendingChoiceReplayLayer layer,
        PrimaryChoiceMatch? unresolvedPrimaryChoice,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<DeferredOccurrenceChoiceBranch> occurrenceCollector,
        PrimaryChoiceReplayFrontier? replayedChoices = null)
    {
        using var checkpointOwner = layer;
        ExecutionChoiceReplayCheckpoint? previous = _executionChoiceReplayCheckpoint;
        _executionChoiceReplayCheckpoint = layer.Checkpoint;
        try
        {
        for (int index = 0; index < layer.Branches.Count; index++)
        {
            ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                searchBudget,
                layer.Branches.Count - index - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(layer.Branches.Count - index);
                break;
            }
            PendingChoiceReplayBranch branch = layer.Branches[index];
            if (replayedChoices == null && !TrySpendChoiceReplayAttempt(branchBudget))
                break;
            SimulationSnapshot? resolvedSnapshot = replayedChoices == null
                ? ReplayPendingChoiceBranch(node, branch)
                : replayedChoices.Take(index, branchBudget);
            if (resolvedSnapshot == null)
                continue;
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         branch.Action,
                         resolvedSnapshot,
                         unresolvedPrimaryChoice,
                         branchBudget,
                         occurrenceCollector))
            {
                yield return (finalAction, finalSnapshot);
            }
        }
        }
        finally { _executionChoiceReplayCheckpoint = previous; }
    }

    private SimulationSnapshot? ReplayPendingChoiceBranch(
        SearchNode node,
        PendingChoiceReplayBranch branch,
        ReplayForkSeed? replayForkSeed = null)
        => branch.PruneInvalidBranch
            ? ReplayPlannedChoiceBranch(node, branch.Action, replayForkSeed)
            : ReplayAction(node, branch.Action, replayForkSeed);

}
