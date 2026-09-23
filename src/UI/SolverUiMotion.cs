using Godot;

namespace CombatSolver;

// Frame-rate independent easing for overlay readouts. The values it smooths are display-only:
// search, logging and tests keep reading the sampled targets, never the eased values.
internal static class SolverUiMotion
{
    // Time for a readout to close about 63% of the gap to a new sample. Memory samples arrive every
    // 0.25 s, so this keeps the bar visibly moving between samples without lagging a full sample.
    internal const double ReadoutTimeConstantSeconds = 0.18d;

    internal static double Blend(double delta, double timeConstantSeconds)
        => delta <= 0d ? 0d : 1d - Math.Exp(-delta / timeConstantSeconds);

    internal static double Approach(double current, double target, double blend, double snapDistance)
    {
        double next = current + (target - current) * blend;
        return Math.Abs(target - next) <= snapDistance ? target : next;
    }

    internal static Color Approach(Color current, Color target, double blend)
    {
        Color next = current.Lerp(target, (float)blend);
        return Math.Abs(next.R - target.R) + Math.Abs(next.G - target.G)
            + Math.Abs(next.B - target.B) + Math.Abs(next.A - target.A) < 0.004f
                ? target
                : next;
    }
}
