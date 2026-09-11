using Godot;

namespace CombatSolver;

internal readonly record struct SolverActionBarState(bool Collapsed, bool Searching, bool ShowAdopt);

// Owns layout only. The overlay retains command bindings and capability checks.
internal sealed partial class SolverActionBar : VBoxContainer
{
    private readonly HFlowContainer _actions;
    private readonly Control _autoStart;
    private readonly HBoxContainer _memoryRow;
    private readonly Button _execute;
    private readonly Button _recalculate;
    private readonly Button _stop;
    private readonly Button _adopt;
    private readonly Button _fullAuto;
    private readonly Control _memory;

    public SolverActionBar(Button execute, Button recalculate, Button stop, Button adopt,
        Button fullAuto, Control autoStart, Control memory, Button releaseMemory)
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _execute = execute;
        _recalculate = recalculate;
        _stop = stop;
        _adopt = adopt;
        _fullAuto = fullAuto;
        _memory = memory;
        _actions = CreateFlow("CombatActions");
        _autoStart = autoStart;
        HBoxContainer actionRow = new() { Name = "ActionRow", MouseFilter = MouseFilterEnum.Pass };
        actionRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _actions.AddChild(fullAuto);
        _actions.AddChild(adopt);
        _actions.AddChild(execute);
        _actions.AddChild(recalculate);
        _actions.AddChild(stop);
        actionRow.AddChild(_actions);
        actionRow.AddChild(autoStart);
        AddChild(actionRow);
        _memoryRow = new HBoxContainer { Name = "MemoryRow", MouseFilter = MouseFilterEnum.Pass };
        _memoryRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _memoryRow.AddChild(memory);
        _memoryRow.AddChild(releaseMemory);
        AddChild(_memoryRow);
    }

    public void Refresh(SolverActionBarState state)
    {
        _recalculate.Visible = !state.Searching;
        _stop.Visible = state.Searching;
        _adopt.Visible = !state.Collapsed && state.ShowAdopt;
        _execute.Visible = !state.Searching;
        _autoStart.Visible = !state.Collapsed;
        _memoryRow.Visible = !state.Collapsed;
        _memory.Visible = !state.Collapsed;
    }

    internal void AssertLayoutForTesting()
    {
        foreach (bool collapsed in new[] { false, true })
        foreach (bool searching in new[] { false, true })
        foreach (bool adopt in new[] { false, true })
        {
            Refresh(new SolverActionBarState(collapsed, searching, adopt));
            if (_stop.Visible != searching || _recalculate.Visible == searching
                || _adopt.Visible != (!collapsed && adopt)
                || _execute.Visible == searching
                || _memory.Visible == collapsed || _autoStart.Visible == collapsed
                || _memoryRow.Visible == collapsed
                || _fullAuto.GetParent() != _actions || _fullAuto.GetIndex() != 0
                || _adopt.GetIndex() != 1)
                throw new InvalidOperationException("Action bar layout state did not match its display snapshot.");
        }
    }

    private static HFlowContainer CreateFlow(string name)
    {
        HFlowContainer flow = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        flow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        flow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        return flow;
    }
}
