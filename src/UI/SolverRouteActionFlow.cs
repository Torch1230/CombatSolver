using Godot;

namespace CombatSolver;

// Route items wrap as units; a loop can also wrap its own actions. Keep the
// minimum width independent of the loop's preferred single-line width.
internal sealed partial class SolverRouteActionFlow : Container
{
    private float _height;

    public override Vector2 _GetMinimumSize()
        => new(GetChildren().OfType<Control>().Where(child => child.Visible)
            .Select(child => child.GetCombinedMinimumSize().X).DefaultIfEmpty(0).Max(), _height);

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren) return;
        float horizontal = GetThemeConstant("h_separation");
        float vertical = GetThemeConstant("v_separation");
        float x = 0, y = 0, lineHeight = 0;
        foreach (Control child in GetChildren().OfType<Control>().Where(child => child.Visible))
        {
            Vector2 minimum = child.GetCombinedMinimumSize();
            float preferred = child is SolverLoopGroup loop ? loop.NaturalWidth : minimum.X;
            float width = Mathf.Max(minimum.X, Mathf.Min(preferred, Size.X));
            if (x > 0 && x + width > Size.X)
            {
                x = 0;
                y += lineHeight + vertical;
                lineHeight = 0;
            }
            FitChildInRect(child, new Rect2(x, y, width, minimum.Y));
            x += width + horizontal;
            lineHeight = Mathf.Max(lineHeight, minimum.Y);
        }
        float height = y + lineHeight;
        if (!Mathf.IsEqualApprox(_height, height))
        {
            _height = height;
            UpdateMinimumSize();
        }
    }
}
