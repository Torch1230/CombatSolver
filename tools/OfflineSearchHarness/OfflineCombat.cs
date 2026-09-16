using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace OfflineSearchHarness;

internal sealed record HarnessScenario(
    string CharacterId,
    string EncounterId,
    string Seed,
    int Ascension,
    int ActIndexForTest);

/// <summary>
/// 用游戏自己的对象图建一场战斗并推进到玩家第一回合。步骤照
/// <c>NGame.StartNewSingleplayerRun</c> + <c>RunManager.EnterRoomDebug</c> + <c>CombatRoom.StartCombat</c>，
/// 只有「需要 Godot 节点/资源」的那几步被绕过（见 <see cref="GameBootstrap.Bypasses"/>）。
/// </summary>
internal static class OfflineCombat
{
    public static async Task EnterCombatRoomAsync(HarnessScenario scenario)
    {
        CharacterModel character = ResolveUnique(ModelDb.AllCharacters, scenario.CharacterId, "角色");
        EncounterModel encounter = ResolveUnique(
            ModelDb.All.OfType<EncounterModel>(), scenario.EncounterId, "遭遇");

        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("已经有进行中的跑局。");

        UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        RunState runState = RunState.CreateForNewRun(
            [Player.CreateForNewRun(character, unlockState, 1uL)],
            ModelDb.ActsByIndex is { } ? ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList() : [],
            [],
            GameMode.Standard,
            scenario.Ascension,
            scenario.Seed);

        HarnessLog.Trace("run_state_created");
        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        HarnessLog.Trace("set_up_new_singleplayer");
        await RunManager.Instance.FinalizeStartingRelics();
        HarnessLog.Trace("finalize_starting_relics");
        RunManager.Instance.Launch();
        HarnessLog.Trace("launch");
        await RunManager.Instance.EnterAct(0, doTransition: false);
        HarnessLog.Trace("enter_act_0");

        if (scenario.ActIndexForTest != 0)
        {
            if ((uint)scenario.ActIndexForTest >= (uint)runState.Acts.Count)
                throw new InvalidOperationException($"幕索引超出范围：{scenario.ActIndexForTest}。");
            await RunManager.Instance.SetActInternal(scenario.ActIndexForTest);
        }

        HarnessLog.Trace("act_index_applied");
        EncounterModel mutableEncounter = encounter.ToMutable();
        HarnessLog.Trace("encounter_mutable");
        await RunManager.Instance.EnterRoomDebug(
            RoomType.Monster,
            MapPointType.Unassigned,
            mutableEncounter);
        HarnessLog.Trace("enter_room_debug");
    }

    /// <summary>
    /// 回合循环是 <c>AfterCombatRoomLoaded</c> 里 fire-and-forget 起来的，所以这里只能靠泵消息循环
    /// 把它推到「玩家出牌阶段」。判定条件与游戏内 <c>WaitForPlayableCombatAsync</c> 一致。
    /// </summary>
    public static CombatState WaitForPlayableCombat(MainLoopContext loop)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (state != null
                && CombatManager.Instance.IsInProgress
                && state.CurrentSide == CombatSide.Player
                && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                return state;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "等玩家出牌阶段超时。" +
                    $"in_progress={CombatManager.Instance.IsInProgress} " +
                    $"side={state?.CurrentSide.ToString() ?? "-"} " +
                    $"phase={player?.PlayerCombatState?.Phase.ToString() ?? "-"}");
            }
            loop.Pump(TimeSpan.FromMilliseconds(30));
        }
    }

    public static string DescribeRoot(CombatState state)
    {
        Player player = LocalContext.GetMe(state)!;
        PlayerCombatState combat = player.PlayerCombatState!;
        string hand = string.Join(",", combat.Hand.Cards.Select(card => card.Id.Entry));
        string enemies = string.Join(",", state.Enemies.Select(enemy =>
            $"{enemy.Monster?.Id.Entry ?? enemy.Name}#{enemy.CombatId}:{enemy.CurrentHp}/{enemy.MaxHp}"
            + $"@{DescribeIntent(enemy)}"));
        return $"turn={combat.TurnNumber} round={state.RoundNumber} phase={combat.Phase} side={state.CurrentSide} "
            + $"energy={combat.Energy} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} "
            + $"hand=[{hand}] draw={combat.DrawPile.Cards.Count} discard={combat.DiscardPile.Cards.Count} "
            + $"relics=[{string.Join(",", player.Relics.Select(relic => relic.Id.Entry))}] "
            + $"enemies=[{enemies}] total_floor={state.RunState.TotalFloor} act_floor={state.RunState.ActFloor}";
    }

    private static string DescribeIntent(Creature enemy)
    {
        try
        {
            return enemy.Monster?.NextMove?.Id ?? "-";
        }
        catch (Exception error)
        {
            return $"<{error.GetType().Name}>";
        }
    }

    private static TModel ResolveUnique<TModel>(IEnumerable<TModel> candidates, string input, string kind)
        where TModel : AbstractModel
    {
        TModel[] matches = candidates
            .Where(candidate => candidate.Id.ToString().Equals(input, StringComparison.OrdinalIgnoreCase)
                || candidate.Id.Entry.Equals(input, StringComparison.OrdinalIgnoreCase)
                || candidate.GetType().Name.Equals(input, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(candidate => candidate.Id)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"找不到{kind} {input}。"),
            _ => throw new InvalidOperationException($"{kind} {input} 不唯一。"),
        };
    }
}
