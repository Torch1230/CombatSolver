using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertGeneratedScenarioResolver()
    {
        var input = new GeneratedCombatScenarioOptions { Seed = "GENERATOR-CONTRACT-1", Mode = "Setup" };
        var first = GeneratedCombatScenario.Resolve(input);
        string Describe(GeneratedCombatScenarioOptions spec) => JsonSerializer.Serialize(spec, GeneratedCombatScenario.JsonOptions);
        string expected = Describe(first.Options);
        if (expected != Describe(GeneratedCombatScenario.Resolve(input).Options)
            || expected != Describe(GeneratedCombatScenario.Resolve(first.Options).Options))
            throw new InvalidOperationException("随机种子或解析后配置不能稳定复现。");
        var otherPotions = GeneratedCombatScenario.Resolve(input with { Potions = new() { Count = 0 } });
        if (expected != Describe(otherPotions.Options with { Potions = first.Options.Potions }))
            throw new InvalidOperationException("药水选择改变了其他类别的随机流。");
        foreach (var character in ModelDb.AllCharacters.Where(c => c.GetType().Assembly == typeof(CharacterModel).Assembly))
        {
            var result = GeneratedCombatScenario.Resolve(input with { CharacterId = character.Id.Entry });
            if (result.Options.CharacterCards.Ids.Any(id => !character.CardPool.AllCards.Any(c => c.Id.Entry == id))
                || result.Options.Relics.Ids.Distinct().Count() != result.Options.Relics.Ids.Length
                || result.Options.Relics.Ids.Any(id => character.StartingRelics.Any(r => r.Id.Entry == id)))
                throw new InvalidOperationException("角色牌池、遗物唯一性或初始遗物排除失败。");
        }
        var request = new UnattendedTestRequest { SearchBudgetOverrideMilliseconds = 765, FixedSearchBudget = false };
        var applied = GeneratedCombatScenario.Apply(request, first);
        if (request.FixedSearchBudget || !applied.FixedSearchBudget || applied.SearchBudgetOverrideMilliseconds != 765
            || !applied.StopAfterCombatRootSnapshotAssertion || !applied.PreserveNativeCombatStateForTest)
            throw new InvalidOperationException("生成请求未保留指定预算、隔离原请求或选择正确建局模式。");
        var deployed = GeneratedCombatScenario.Apply(request, first with { Options = first.Options with { Mode = "Deploy" } });
        if (deployed.ExpectedUnexpectedReplansAtMost != 0 || deployed.StopAfterCombatRootSnapshotAssertion
            || deployed.StopAfterInitialSolverResultAssertion)
            throw new InvalidOperationException("生成部署未要求零计划外重算或被错误提前终止。");
        bool rejectedHold = false;
        try { GeneratedCombatScenario.Apply(new() { HoldAfterInitialSearch = true }, first); }
        catch (InvalidDataException) { rejectedHold = true; }
        if (!rejectedHold) throw new InvalidOperationException("生成模式错误接受Hold模式。");
        string fixedRelic = first.Options.Relics.Ids[0]!;
        var partial = GeneratedCombatScenario.Resolve(input with
            { Relics = new() { Count = 3, Ids = [null, fixedRelic] } });
        if (partial.Options.Relics.Ids[1] != fixedRelic || partial.Options.Relics.Ids.Distinct().Count() != 3)
            throw new InvalidOperationException("部分指定遗物被随机槽占用或重复。");
        void Reject(GeneratedCombatScenarioOptions invalid)
        {
            try { GeneratedCombatScenario.Resolve(invalid); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("生成器接受了无效请求。");
        }
        Reject(input with { Ascension = 11 });
        Reject(input with { CharacterCards = new() { Count = -1 } });
        Reject(input with { CharacterCards = new() { Ids = ["NOT_A_REAL_CARD"] } });
        Reject(input with { Relics = new() { Ids = [fixedRelic, fixedRelic] } });
        Reject(input with { Relics = new() { Count = 101 } });
        Reject(input with { Potions = new() { Count = 0, Ids = ["FIRE_POTION"] } });
        return "GeneratedResolver:RepeatSeed:ExplicitReplay:IndependentStreams:AllCharacters:PartialIds:RequestIsolation:Budget:DeployReplans:RejectInvalid";
    }
}
