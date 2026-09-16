using System.Text.Json;
using CombatSolver;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace OfflineSearchHarness;

/// <summary>
/// 生成场景的离线开局流程。每一步都走模组自己的 <c>GeneratedCombatScenario</c> 与
/// <c>UnattendedTestRunner.OfflineScenarioSession</c>（转出的是 <c>ScenarioBuilder</c> 的原方法），
/// 顺序照 <c>UnattendedTestRunner.ScenarioBuilder.BuildAsync</c>：
/// Resolve → Apply → 推开局选牌选择器 → 新跑局 → SetActInternal → PrepareGeneratedStartingRelics →
/// InjectRelicAsync* → ClearRunDeck → PrepareGeneratedAscendersBaneAsync → InjectRunCardAsync* →
/// PrepareGeneratedPotionSlots → InjectPotionForTest* → CaptureGeneratedLoadout →
/// EnterRoomDebug → 玩家第一回合 → CaptureGeneratedOpening。
/// 只有「需要 Godot 节点/资源」的入口被 <see cref="GameBootstrap"/> 绕过。
/// </summary>
internal sealed class GeneratedScenarioSetup
{
    public UnattendedTestRunner.OfflineScenarioSession Session { get; }
    public ResolvedGeneratedCombatScenario Resolved { get; }
    public UnattendedTestRequest Request => Session.Request;

    private GeneratedScenarioSetup(
        UnattendedTestRunner.OfflineScenarioSession session, ResolvedGeneratedCombatScenario resolved)
    {
        Session = session;
        Resolved = resolved;
    }

    public static UnattendedTestRequest ReadRequest(string path)
        => JsonSerializer.Deserialize<UnattendedTestRequest>(
            File.ReadAllText(path), UnattendedTestFiles.JsonOptions)
           ?? throw new InvalidDataException($"请求文件为空：{path}。");

    /// <summary>请求是 class 不是 record，只能走 JSON 往返改字段。</summary>
    public static UnattendedTestRequest WithEvidenceDirectory(UnattendedTestRequest request, string directory)
    {
        System.Text.Json.Nodes.JsonObject json =
            JsonSerializer.SerializeToNode(request, UnattendedTestFiles.JsonOptions)!.AsObject();
        json["evidenceDirectory"] = directory;
        return json.Deserialize<UnattendedTestRequest>(UnattendedTestFiles.JsonOptions)!;
    }

    /// <summary>照 <c>PrepareGeneratedScenario</c>：读规格 → <c>Resolve</c> → <c>Apply</c>，并落盘同名产物。</summary>
    public static GeneratedScenarioSetup Prepare(UnattendedTestRequest request, string evidenceDirectory)
    {
        string path = request.GeneratedScenarioPath
            ?? throw new InvalidDataException("请求没有 generatedScenarioPath。");
        var options = JsonSerializer.Deserialize<GeneratedCombatScenarioOptions>(
            File.ReadAllText(path), GeneratedCombatScenario.JsonOptions)
            ?? throw new InvalidDataException("生成场景配置为空。");
        ResolvedGeneratedCombatScenario resolved = GeneratedCombatScenario.Resolve(options);
        // 证据目录先换成本根自己的目录（Apply 不碰这个字段，先后顺序不影响解析结果）。
        UnattendedTestRequest applied = GeneratedCombatScenario.Apply(
            WithEvidenceDirectory(request, evidenceDirectory), resolved);

        UnattendedTestRunner.OfflineScenarioSession session =
            UnattendedTestRunner.OfflineScenarioSession.Create(applied, resolved);
        session.WriteGeneratedArtifact("generated-scenario.resolved.json", resolved.Options);
        session.WriteGeneratedArtifact("generated-scenario.catalog.json", resolved.Catalog);
        session.WriteGeneratedArtifact("generated-scenario.request.json", applied);
        session.AddCompletedCheck("GeneratedScenario:Resolved:NativePools:IndependentGeneratorRng");
        return new GeneratedScenarioSetup(session, resolved);
    }

    /// <summary>建跑局、注入装备、进房。返回的作用域在整场战斗期间都不能释放。</summary>
    public async Task<IDisposable?> EnterCombatRoomAsync()
    {
        UnattendedTestRequest request = Request;
        IDisposable? choiceScope = Session.BeginSetupChoices();
        try
        {
            CharacterModel character = ResolveUnique(ModelDb.AllCharacters, request.CharacterId, "角色");
            EncounterModel encounter = ResolveUnique(
                ModelDb.All.OfType<EncounterModel>(), request.EncounterId, "遭遇");

            if (RunManager.Instance.IsInProgress)
                throw new InvalidOperationException("已经有进行中的跑局。");

            HarnessLog.Trace("gen.start_run");
            UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
            RunState runState = RunState.CreateForNewRun(
                [Player.CreateForNewRun(character, unlockState, 1uL)],
                ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList(),
                [],
                GameMode.Standard,
                request.Ascension,
                request.Seed);
            RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
            await RunManager.Instance.FinalizeStartingRelics();
            RunManager.Instance.Launch();
            await RunManager.Instance.EnterAct(0, doTransition: false);
            HarnessLog.Trace("gen.run_started");

            if (request.ActIndexForTest != 0)
            {
                if ((uint)request.ActIndexForTest >= (uint)runState.Acts.Count)
                    throw new InvalidOperationException($"测试幕索引超出范围：{request.ActIndexForTest}。");
                await RunManager.Instance.SetActInternal(request.ActIndexForTest);
            }
            HarnessLog.Trace("gen.act_applied");

            Player runPlayer = LocalContext.GetMe(runState)
                ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");

            Session.PrepareStartingRelics(runPlayer);
            foreach (UnattendedRelicInjection injection in request.Relics)
                await UnattendedTestRunner.OfflineScenarioSession.InjectRelicAsync(runPlayer, injection);
            HarnessLog.Trace($"gen.relics_injected relics={runPlayer.Relics.Count}");
            if (request.ClearRunDeck)
                UnattendedTestRunner.OfflineScenarioSession.ClearRunDeck(runState, runPlayer);
            await Session.PrepareAscendersBaneAsync(runState, runPlayer);
            foreach (UnattendedCardInjection injection in request.RunCards)
                await UnattendedTestRunner.OfflineScenarioSession.InjectRunCardAsync(runState, runPlayer, injection);
            HarnessLog.Trace($"gen.cards_injected deck={runPlayer.Deck.Cards.Count}");
            Session.PreparePotionSlots(runPlayer);
            if (request.PreserveNativeCombatStateForTest)
                foreach (UnattendedPotionInjection injection in request.Potions)
                    UnattendedTestRunner.OfflineScenarioSession.InjectPotionForTest(runPlayer, injection.PotionId);
            Session.CaptureLoadout(runState, runPlayer);
            HarnessLog.Trace("gen.loadout_captured");

            EncounterModel mutableEncounter = encounter.ToMutable();
            MapPointType targetMapPointType = request.TargetMapPointType != MapPointType.Unassigned
                ? request.TargetMapPointType
                : request.TargetRoomType switch
                {
                    RoomType.Monster => MapPointType.Monster,
                    RoomType.Elite => MapPointType.Elite,
                    RoomType.Boss => MapPointType.Boss,
                    _ => throw new InvalidOperationException($"只支持战斗房间，收到 {request.TargetRoomType}。"),
                };
            await RunManager.Instance.EnterRoomDebug(
                request.TargetRoomType, targetMapPointType, mutableEncounter);
            HarnessLog.Trace("gen.entered_room");
            return choiceScope;
        }
        catch
        {
            choiceScope?.Dispose();
            throw;
        }
    }

    /// <summary>照 <c>CaptureGeneratedOpening</c>：校验遭遇/选牌消费完，并落盘 opening 产物。</summary>
    public void CaptureOpening(CombatState combat)
        => Session.CaptureOpening(combat, LocalContext.GetMe(combat)!);

    public string[][] SetupChoices => Session.SetupChoices;

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
