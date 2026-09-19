using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<Creature, int>? _knowledgeDemonCurseCounters;

    public KnowledgeDemonChoiceRequest? PendingKnowledgeDemonChoice { get; private set; }

    public int GetKnowledgeDemonCurseCounter(Creature source)
    {
        if (_knowledgeDemonCurseCounters?.TryGetValue(source, out int predicted) == true)
            return predicted;
        if (_rootMaterialized && _rootCreatures.Contains(source))
            throw new InvalidOperationException($"Knowledge Demon root state was not captured for {source.Name}.");
        int live = MonsterValueReader.ReadInt(source.Monster
            ?? throw new InvalidOperationException("知识恶魔选择来源没有怪物模型。"), "_curseOfKnowledgeCounter");
        (_knowledgeDemonCurseCounters ??= [])[source] = live;
        return live;
    }

    public void AdvanceKnowledgeDemonCurseCounter(Creature source)
        => (_knowledgeDemonCurseCounters ??= [])[source] = GetKnowledgeDemonCurseCounter(source) + 1;

    public void SetPendingKnowledgeDemonChoice(KnowledgeDemonChoiceRequest request)
    {
        if (PendingKnowledgeDemonChoice != null)
            throw new InvalidOperationException("模拟状态已经存在待处理的知识恶魔选择。");
        PendingKnowledgeDemonChoice = request;
    }

    public void ClearPendingKnowledgeDemonChoice()
        => PendingKnowledgeDemonChoice = null;
}
