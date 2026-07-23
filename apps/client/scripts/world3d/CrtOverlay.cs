using Godot;

namespace Ashfall.Client;

/// <summary>
/// The design's screen grade laid over the 3D world (and beneath the HUD, so the
/// interface never dims): a vertical time-of-day colour tint and faint CRT
/// scanlines. It reads the same day phase the sky uses, so the world's light and
/// the frame's mood move together — cool at noon, ember at dusk, deep blue at
/// night — exactly like the design mock's <c>timeTint</c>.
/// </summary>
public partial class CrtOverlay : CanvasLayer
{
    // Two-stop vertical tints per phase — top of screen, then bottom (with alpha).
    private static readonly Color DayTop = new(0.12f, 0.16f, 0.25f, 0.12f);
    private static readonly Color DayBottom = new(0.08f, 0.07f, 0.06f, 0.30f);
    private static readonly Color DuskTop = new(0.16f, 0.08f, 0.04f, 0.34f);
    private static readonly Color DuskBottom = new(0.05f, 0.04f, 0.03f, 0.66f);
    private static readonly Color NightTop = new(0.04f, 0.05f, 0.13f, 0.58f);
    private static readonly Color NightBottom = new(0.03f, 0.04f, 0.09f, 0.82f);

    private Gradient _gradient = null!;

    public override void _Ready()
    {
        var tint = new TextureRect
        {
            Texture = BuildGradient(),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        tint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(tint);

        var scanlines = new TextureRect
        {
            Texture = BuildScanlines(),
            StretchMode = TextureRect.StretchModeEnum.Tile,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        scanlines.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(scanlines);
    }

    /// <summary>Blends the phase tints; sunrise 0.25, noon 0.5, sunset 0.75.</summary>
    public void SetTimeOfDay(float timeOfDay)
    {
        float daylight = Mathf.Sin((timeOfDay - 0.25f) * Mathf.Tau);
        float day = Mathf.Clamp(daylight, 0f, 1f);
        float night = Mathf.Clamp(-daylight, 0f, 1f);
        float dusk = 1f - day - night;

        _gradient.SetColor(0, Blend(DayTop, DuskTop, NightTop, day, dusk, night));
        _gradient.SetColor(1, Blend(DayBottom, DuskBottom, NightBottom, day, dusk, night));
    }

    private static Color Blend(Color a, Color b, Color c, float wa, float wb, float wc) =>
        new(a.R * wa + b.R * wb + c.R * wc,
            a.G * wa + b.G * wb + c.G * wc,
            a.B * wa + b.B * wb + c.B * wc,
            a.A * wa + b.A * wb + c.A * wc);

    private GradientTexture2D BuildGradient()
    {
        _gradient = new Gradient();
        _gradient.SetColor(0, DuskTop);
        _gradient.SetColor(1, DuskBottom);
        return new GradientTexture2D
        {
            Gradient = _gradient,
            Width = 1,
            Height = 64,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(0, 1),
        };
    }

    /// <summary>One dark scanline per three rows, matching the mock's 1px-on/2px-off.</summary>
    private static ImageTexture BuildScanlines()
    {
        var image = Image.CreateEmpty(1, 3, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(0, 0, 0, 0.18f));
        image.SetPixel(0, 1, new Color(0, 0, 0, 0));
        image.SetPixel(0, 2, new Color(0, 0, 0, 0));
        return ImageTexture.CreateFromImage(image);
    }
}
