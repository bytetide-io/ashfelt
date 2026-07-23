using Godot;

namespace Ashfall.Client;

/// <summary>
/// A survival-meter fill drawn the Ashfall way: a flat colour crossed by the
/// design's dither stripes (the pixel equivalent of the spec's
/// <c>repeating-linear-gradient</c>), so a bar reads as chunky pixel art rather
/// than a smooth gradient. The owner sizes the bar to the current fraction and
/// the stripes tile from the left, exactly like the mock.
/// </summary>
public partial class StripedBar : Control
{
    private const int StripeStep = 7; // gap between dark stripes, in pixels
    private const int StripeWidth = 2; // width of each dark stripe
    private static readonly Color Stripe = new(0, 0, 0, 0.25f);

    private Color _fill = DesignSystem.Ember;

    public Color Fill
    {
        get => _fill;
        set { _fill = value; QueueRedraw(); }
    }

    public StripedBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (rect.Size.X <= 0 || rect.Size.Y <= 0) return;

        DrawRect(rect, _fill);
        for (float x = StripeStep; x < rect.Size.X; x += StripeStep)
            DrawRect(new Rect2(x, 0, StripeWidth, rect.Size.Y), Stripe);
    }
}
