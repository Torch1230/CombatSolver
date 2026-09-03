using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Simulation;

internal readonly record struct RecordedRelicTrigger(string RelicId, string Summary);
internal readonly record struct RecordedKill(uint CombatId, string TargetId, CombatDamageSource Source);

/// <summary>
/// Enabled only for the single final-route replay. Normal Beam expansion keeps this null, so
/// displaying relic and kill provenance does not add a list or string allocation to every transition.
/// </summary>
internal sealed class ActionRelicTriggerRecorder
{
    private readonly Dictionary<int, List<RecordedRelicTrigger>> _triggers = [];
    private readonly Dictionary<int, List<RecordedKill>> _kills = [];
    private int _actionIndex = -1;

    public void BeginAction(int actionIndex) => _actionIndex = actionIndex;

    public void Record(RelicModel relic, string summary)
    {
        if (_actionIndex < 0)
            throw new InvalidOperationException("Relic trigger was recorded outside a planned action.");
        RecordedRelicTrigger trigger = new(relic.Id.Entry, summary);
        if (!_triggers.TryGetValue(_actionIndex, out List<RecordedRelicTrigger>? entries))
        {
            entries = [];
            _triggers.Add(_actionIndex, entries);
        }
        if (!entries.Contains(trigger))
            entries.Add(trigger);
    }

    public IReadOnlyList<RecordedRelicTrigger> ForAction(int actionIndex)
        => _triggers.GetValueOrDefault(actionIndex) ?? [];

    public void RecordKill(uint combatId, string targetId, CombatDamageSource source)
    {
        if (_actionIndex < 0)
            throw new InvalidOperationException("击杀来源记录发生在计划动作之外。");
        if (string.IsNullOrEmpty(targetId))
            throw new InvalidOperationException("击杀来源记录缺少目标模型 ID。");
        RecordedKill kill = new(combatId, targetId, source);
        if (!_kills.TryGetValue(_actionIndex, out List<RecordedKill>? entries))
        {
            entries = [];
            _kills.Add(_actionIndex, entries);
        }
        if (!entries.Contains(kill))
            entries.Add(kill);
    }

    public IReadOnlyList<RecordedKill> KillsForAction(int actionIndex)
        => _kills.GetValueOrDefault(actionIndex) ?? [];
}
