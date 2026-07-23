using Godot;

namespace Ashfall.Client;

/// <summary>
/// The single source of truth for the Ashfall look, ported from the
/// "Ashfall Design System" — colours, fonts, spacing and the chunky pixel
/// controls. Every HUD and menu builds from here so nothing re-invents a
/// shade or a button; change a token and the whole client follows.
/// </summary>
public static class DesignSystem
{
    // ---- Surfaces & text (Ink ramp → Parchment) ------------------------
    public static readonly Color Ink900 = new("100e0b"); // app background
    public static readonly Color Ink800 = new("1c1a15"); // panels
    public static readonly Color Ink700 = new("24211a"); // slots, insets
    public static readonly Color Ink600 = new("2e2a20"); // neutral buttons
    public static readonly Color Line = new("3a352b");    // hairlines, outlines
    public static readonly Color Edge = new("0d0b08");    // hard 2px pixel outline

    public static readonly Color Parchment = new("ece3d2"); // primary text
    public static readonly Color Muted = new("c9bda6");     // secondary text
    public static readonly Color Faint = new("6b6252");     // captions

    // ---- Brand · Ember -------------------------------------------------
    public static readonly Color EmberLight = new("f5b24a");
    public static readonly Color Ember = new("e0842e");
    public static readonly Color EmberDeep = new("b8641f");
    public static readonly Color OnEmber = new("241405"); // text on ember fills

    // ---- Survival meters ----------------------------------------------
    public static readonly Color Hunger = new("c9822f");
    public static readonly Color Stamina = new("3f9d54");
    public static readonly Color Health = new("b83b3b");
    public static readonly Color Warmth = new("d6633c");

    public static readonly Color Danger = new("b83b3b");
    public static readonly Color DangerDeep = new("7d2626");
    public static readonly Color OnDanger = new("f6e4e0");

    // ---- Spacing & sizing (mobile, one-thumb reach) -------------------
    public const int Space = 16;      // base gutter / panel margin
    public const int SpaceSm = 8;
    public const int SpaceLg = 24;
    public const int TouchTarget = 60; // comfortable one-thumb tap height
    public const int RoundButton = 72;
    public const int Outline = 2;      // the signature 2px pixel edge
    public const int ChunkyLift = 4;   // button bottom-shadow depth

    public const int BodySize = 16;
    public const int LabelSize = 13;   // Silkscreen kickers / HUD labels
    public const int NumeralSize = 14; // Silkscreen HUD numbers

    private static FontFile? _display; // Silkscreen — logo, labels, numerals
    private static FontFile? _body;    // Pixelify Sans — UI & body

    /// <summary>Silkscreen: logo, headings, HUD numerals, short labels. Never body copy.</summary>
    public static FontFile? Display => _display ??= Load("res://art/fonts/Silkscreen-Regular.ttf");

    /// <summary>Pixelify Sans: buttons, body copy, everything conversational.</summary>
    public static FontFile? Body => _body ??= Load("res://art/fonts/PixelifySans.ttf");

    private static FontFile? Load(string path)
    {
        // The imported FontFile is present once the project has been opened in
        // the editor; fall back to the engine default rather than crash if not.
        if (ResourceLoader.Exists(path)) return ResourceLoader.Load<FontFile>(path);
        GD.PushWarning($"[design] font missing, using default: {path}");
        return null;
    }

    /// <summary>
    /// A theme any menu/HUD root can adopt so buttons, labels and panels inherit
    /// the Ashfall styling without per-control wiring.
    /// </summary>
    public static Theme BuildTheme()
    {
        var theme = new Theme();
        if (Body is { } body)
        {
            theme.DefaultFont = body;
            theme.DefaultFontSize = BodySize;
        }

        theme.SetColor("font_color", "Label", Parchment);

        theme.SetStylebox("normal", "Button", EmberButton());
        theme.SetStylebox("hover", "Button", EmberButton(EmberLight));
        theme.SetStylebox("pressed", "Button", EmberButtonPressed());
        theme.SetStylebox("disabled", "Button", DisabledButton());
        theme.SetColor("font_color", "Button", OnEmber);
        theme.SetColor("font_hover_color", "Button", OnEmber);
        theme.SetColor("font_pressed_color", "Button", OnEmber);
        theme.SetColor("font_disabled_color", "Button", new Color(OnEmber, 0.5f));
        return theme;
    }

    // ---- Chunky pixel controls ----------------------------------------

    /// <summary>
    /// The signature button box: a flat fill, a hard 2px ink outline, and a
    /// solid offset "shadow" that reads as depth without any blur — the pixel
    /// equivalent of the design's <c>box-shadow: 0 4px 0 deep, 0 0 0 2px ink</c>.
    /// </summary>
    public static StyleBoxFlat Chunky(Color fill, Color shadow, int lift = ChunkyLift)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = Edge,
            ShadowColor = shadow,
            ShadowSize = 0,
            ShadowOffset = new Vector2(0, lift),
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 13,
            ContentMarginBottom = 13 + lift,
        };
        box.SetBorderWidthAll(Outline);
        return box;
    }

    /// <summary>The same box pressed in: shadow collapses, content sinks with it.</summary>
    public static StyleBoxFlat ChunkyPressed(Color fill, Color shadow, int lift = ChunkyLift)
    {
        var box = Chunky(fill, shadow, lift);
        box.ShadowOffset = new Vector2(0, 1);
        box.ContentMarginTop = 13 + (lift - 1);
        box.ContentMarginBottom = 13 + 1;
        return box;
    }

    public static StyleBoxFlat EmberButton(Color? fill = null) => Chunky(fill ?? Ember, EmberDeep);
    public static StyleBoxFlat EmberButtonPressed() => ChunkyPressed(Ember, EmberDeep);
    public static StyleBoxFlat NeutralButton() => Chunky(Ink600, Ink900 * new Color(1, 1, 1, 1));
    public static StyleBoxFlat DangerButton() => Chunky(Danger, DangerDeep);

    public static StyleBoxFlat DisabledButton()
    {
        var box = Chunky(Ember, EmberDeep);
        box.BgColor = new Color(Ember, 0.38f);
        box.ShadowColor = new Color(EmberDeep, 0.38f);
        return box;
    }

    /// <summary>A ghost button: no fill, just the 2px outline (Settings / Quit).</summary>
    public static StyleBoxFlat GhostButton(Color? outline = null)
    {
        var box = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderColor = outline ?? Line,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 13,
            ContentMarginBottom = 13,
        };
        box.SetBorderWidthAll(Outline);
        return box;
    }

    /// <summary>A recessed panel: dark fill with a hard ink outline.</summary>
    public static StyleBoxFlat Panel(Color? fill = null, int pad = Space)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill ?? Ink800,
            BorderColor = Edge,
            ContentMarginLeft = pad,
            ContentMarginRight = pad,
            ContentMarginTop = pad,
            ContentMarginBottom = pad,
        };
        box.SetBorderWidthAll(Outline);
        return box;
    }

    /// <summary>An inventory/hotbar slot: inset fill, outline turns ember when selected.</summary>
    public static StyleBoxFlat Slot(bool selected = false, bool empty = false)
    {
        var box = new StyleBoxFlat
        {
            BgColor = empty ? new Color("1a1813") : Ink700,
            BorderColor = selected ? Ember : (empty ? Ink700 : Line),
        };
        box.SetBorderWidthAll(Outline);
        return box;
    }

    /// <summary>Round control base (jump / menu): a dark disc with a lifted edge.</summary>
    public static StyleBoxFlat Round(Color? fill = null, int size = RoundButton)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill ?? new Color("1f2129"),
            BorderColor = Line,
            ShadowColor = Edge,
            ShadowSize = 0,
            ShadowOffset = new Vector2(0, ChunkyLift),
        };
        box.SetBorderWidthAll(Outline);
        box.SetCornerRadiusAll(size / 2);
        return box;
    }

    // ---- Small builders shared across screens -------------------------

    /// <summary>A Silkscreen kicker/label in ember, e.g. "CRAFTING", "TAP TO GATHER".</summary>
    public static Label Kicker(string text, Color? color = null, int size = LabelSize)
    {
        var label = new Label { Text = text };
        if (Display is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Ember);
        return label;
    }

    /// <summary>Apply a stylebox set to a plain Button so it reads as an Ashfall control.</summary>
    public static void StyleButton(Button button, StyleBoxFlat normal, StyleBoxFlat pressed, Color textColor)
    {
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", normal);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.AddThemeColorOverride("font_color", textColor);
        button.AddThemeColorOverride("font_hover_color", textColor);
        button.AddThemeColorOverride("font_pressed_color", textColor);
        if (Body is { } font) button.AddThemeFontOverride("font", font);
    }
}
