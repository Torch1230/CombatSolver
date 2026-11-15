using Godot;

namespace CombatSolver;

// Lives under the overlay layer so it is freed with it; drives per-frame easing of the search
// readouts and coalesced responsive layout.
internal sealed partial class SolverOverlayMotionDriver : Node
{
    public override void _Process(double delta)
    {
        SolverOverlay.ApplyQueuedResponsiveLayout();
        SolverOverlay.AdvanceSearchReadouts(delta);
    }
}
