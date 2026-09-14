using Godot;

namespace CombatSolver;

internal sealed partial class SolverOverlayInputBridge : Node
{
    public override void _Input(InputEvent inputEvent)
    {
        if (!Handle(inputEvent))
            return;
        GetViewport().SetInputAsHandled();
    }

    internal bool Handle(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey
            {
                Pressed: true,
                Echo: false,
                CtrlPressed: true,
                AltPressed: false,
                ShiftPressed: false,
                MetaPressed: false,
                Keycode: Key.F9,
            }
            || BugReportUploadDialog.IsOpen)
        {
            return false;
        }
        return SolverOverlay.ToggleVisibilityFromShortcut();
    }
}
