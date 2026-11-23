using System.Text;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver.HotSearch;

/// <summary>
/// Phase 3 Hot-Kernel <see cref="ISolverBackend"/> implementation wired to <see cref="SolverBackendKind.Hot"/>.
/// </summary>
internal sealed class HotSolverBackend : ISolverBackend
{
    public static HotSolverBackend Instance { get; } = new();

    public SolverResult SolveCoordinator(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken,
        Action<SolverProgress>? progressCallback)
    {
        if (policy.IncludeTurnSetup || root.PlayerPhase != PlayerTurnPhase.Play)
        {
            return LegacySolverBackend.Instance.SolveCoordinator(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken,
                progressCallback);
        }

        return HotSearchCoordinator.SolveCoordinator(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken,
            progressCallback);
    }

    public SolverResult SolveSingleBeam(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        if (policy.IncludeTurnSetup || root.PlayerPhase != PlayerTurnPhase.Play)
        {
            return LegacySolverBackend.Instance.SolveSingleBeam(
                root,
                displayNames,
                battleDamage,
                policy,
                cancellationToken);
        }

        return HotSearchCoordinator.SolveSingleBeam(
            root,
            displayNames,
            battleDamage,
            policy,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates end-of-turn player + enemy-turn risk directly on <see cref="HotCombatEngine"/>
    /// via <see cref="HotCombatEngine.StepEndTurnBeforePlayerStart"/>.
    /// </summary>
    public LiveEndTurnRiskProjection EvaluateEndTurnRisk(
        CombatState liveState,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
    {
        HotRootBundle bundle = HotRootBuilder.BuildFromLive(liveState);
        CombatHotState state = bundle.RootState;
        HotColdSidecar? cold = bundle.RootCold;
        HotRules rules = bundle.Rules;
        HotExecScratch scratch = new();

        HotParallelExpander.AssignDeterministicCardUids(ref state);

        int hpBefore = state.Player.CurrentHp;
        int hpLostBefore = state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
        string monsterMoves = DescribeActiveEnemyMoves(in state, rules);

        // Map any planned PlayerTurnEnd / EnemyTurn card choices into HotChoiceSelection[]
        Span<HotChoiceSelection> choiceBuf = stackalloc HotChoiceSelection[HotLimits.MaxChoicesPerAction];
        int choiceCount = 0;

        if (turnStartChoices is { Count: > 0 })
        {
            for (int i = 0; i < turnStartChoices.Count && choiceCount < HotLimits.MaxChoicesPerAction; i++)
            {
                PlanCardChoice choice = turnStartChoices[i];
                if (choice.Timing is not (PlanChoiceTiming.PlayerTurnEnd or PlanChoiceTiming.EnemyTurn))
                    continue;
                if (choice.Effect == PlanChoiceEffect.ApplyKnowledgeCurse)
                    continue;

                if (choice.Cards.Count == 0)
                {
                    choiceBuf[choiceCount++] = new HotChoiceSelection(HotChoiceKind.None, 0, 0);
                    continue;
                }

                HotChoiceKind kind = choice.SourcePile switch
                {
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand => HotChoiceKind.HandCard,
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Draw => HotChoiceKind.DrawPile,
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard => HotChoiceKind.DiscardPile,
                    MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust => HotChoiceKind.ExhaustPile,
                    _ => HotChoiceKind.HandCard,
                };

                for (int c = 0; c < choice.Cards.Count && choiceCount < HotLimits.MaxChoicesPerAction; c++)
                {
                    PlanCardToken token = choice.Cards[c];
                    HotChoiceSelection resolved = ResolveChoiceToken(in state, cold, rules, kind, token);
                    choiceBuf[choiceCount++] = resolved;
                }
            }
        }

        HotStepResult res = HotCombatEngine.StepEndTurnBeforePlayerStart(
            ref state,
            ref cold,
            rules,
            scratch,
            choiceBuf[..choiceCount]);

        if (res.NeedsChoice)
            throw new InvalidOperationException("结束回合复核产生了路线未提供的选牌。");

        int hpAfter = state.Player.CurrentHp;
        int hpLost = Math.Max(0, state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost] - hpLostBefore);
        bool playerDead = state.TerminalOutcome == HotCombatOutcome.Defeat
            || (state.Player.Flags & HotCreatureFlags.Alive) == 0
            || hpAfter <= 0;

        return new LiveEndTurnRiskProjection(hpBefore, hpAfter, hpLost, playerDead, monsterMoves);
    }

    private static string DescribeActiveEnemyMoves(in CombatHotState s, HotRules rules)
    {
        if (s.RosterCount == 0)
            return string.Empty;

        StringBuilder sb = new(64);
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;

            if (sb.Length > 0)
                sb.Append(',');

            string monsterEntry = enemy.MonsterDefId > 0 && enemy.MonsterDefId < rules.Monsters.Length
                ? rules.Monsters[enemy.MonsterDefId].Entry
                : (eSlot < rules.EnemyMonsterEntries.Length ? rules.EnemyMonsterEntries[eSlot] : "?");

            HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
            string moveId = machine != null && enemy.CurrentMoveNode >= 0 && enemy.CurrentMoveNode < machine.Nodes.Length
                ? machine.Nodes[enemy.CurrentMoveNode].MoveId
                : "null";

            sb.Append(monsterEntry).Append(':').Append(moveId);
        }
        return sb.ToString();
    }

    private static HotChoiceSelection ResolveChoiceToken(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotChoiceKind kind,
        PlanCardToken token)
    {
        HotPileKind pile = kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };

        int start = HotPiles.ZoneStart(in s, pile);
        int count = HotPiles.ZoneCount(in s, pile);
        int seen = 0;

        for (int i = 0; i < count; i++)
        {
            ref readonly HotCard card = ref s.Cards[start + i];
            HotCardDef def = rules.Cards[card.CardDefId];
            if (!string.Equals(def.Entry, token.CardId, StringComparison.Ordinal)
                || card.UpgradeLevel != token.UpgradeLevel)
            {
                continue;
            }
            if (seen == token.OptionOccurrence || seen == token.SourceOccurrence)
                return new HotChoiceSelection(kind, card.CardUid, card.CardDefId);
            seen++;
        }

        ushort fallbackDefId = rules.FindCardDefId(token.CardId);
        return new HotChoiceSelection(kind, 0, fallbackDefId);
    }
}
