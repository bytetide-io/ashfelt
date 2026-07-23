using Godot;

namespace Ashfall.Client;

/// <summary>
/// The Ashfall title screen, built to the design system's TITLE mock: the ash
/// wordmark over the glowing campfire mark, then New World / Continue and the
/// smaller Settings / Quit pair. It is pure presentation — the only state it
/// owns is "which scene comes next" — so it stays a thin gateway into the world.
/// </summary>
public partial class TitleScreen : Control
{
    private const string WorldScene = "res://scenes/World3D.tscn";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = DesignSystem.BuildTheme();

        AddChild(FullRect(new ColorRect { Color = DesignSystem.Ink900 }));

        var centre = new CenterContainer();
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        column.AddThemeConstantOverride("separation", 12);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        centre.AddChild(column);

        var mark = PixelIcons.Make("campfire", 84);
        mark.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        column.AddChild(mark);
        AnimateGlow(mark);

        column.AddChild(Centered(Wordmark()));
        column.AddChild(Centered(DesignSystem.Kicker("GATHER · CRAFT · ENDURE", DesignSystem.Ember, 11)));
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });

        column.AddChild(PrimaryButton("NEW WORLD", () => Enter()));
        column.AddChild(NeutralButton("CONTINUE", () => Enter()));

        var minor = new HBoxContainer();
        minor.AddThemeConstantOverride("separation", 11);
        minor.Alignment = BoxContainer.AlignmentMode.Center;
        minor.AddChild(GhostButton("SETTINGS", () => { }));
        minor.AddChild(GhostButton("QUIT", () => GetTree().Quit()));
        column.AddChild(minor);

        var footer = DesignSystem.Kicker("open-source survival · seed 1337", DesignSystem.Faint, 10);
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(Centered(footer));
    }

    private void Enter() => GetTree().ChangeSceneToFile(WorldScene);

    private static Label Wordmark()
    {
        var label = new Label { Text = "ASHFALL", HorizontalAlignment = HorizontalAlignment.Center };
        if (DesignSystem.Display is { } font) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 52);
        label.AddThemeColorOverride("font_color", new Color("f2e9d8"));
        label.AddThemeColorOverride("font_shadow_color", DesignSystem.EmberDeep);
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        return label;
    }

    private void AnimateGlow(Control node)
    {
        node.Modulate = new Color(1, 1, 1, 0.85f);
        var tween = CreateTween().SetLoops();
        tween.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tween.TweenProperty(node, "modulate:a", 1.0f, 1.6);
        tween.TweenProperty(node, "modulate:a", 0.85f, 1.6);
    }

    private Button PrimaryButton(string text, System.Action onPress)
    {
        var button = FullWidth(text);
        DesignSystem.StyleButton(button, DesignSystem.EmberButton(), DesignSystem.EmberButtonPressed(), DesignSystem.OnEmber);
        button.Pressed += onPress;
        return button;
    }

    private Button NeutralButton(string text, System.Action onPress)
    {
        var button = FullWidth(text);
        DesignSystem.StyleButton(button, DesignSystem.NeutralButton(), DesignSystem.ChunkyPressed(DesignSystem.Ink600, DesignSystem.Edge), DesignSystem.Parchment);
        button.Pressed += onPress;
        return button;
    }

    private Button GhostButton(string text, System.Action onPress)
    {
        var button = new Button { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        DesignSystem.StyleButton(button, DesignSystem.GhostButton(), DesignSystem.GhostButton(DesignSystem.Faint), DesignSystem.Muted);
        button.Pressed += onPress;
        return button;
    }

    private static Button FullWidth(string text) => new()
    {
        Text = text,
        CustomMinimumSize = new Vector2(0, DesignSystem.TouchTarget),
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    private static Control Centered(Control child)
    {
        var wrap = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        wrap.AddChild(child);
        return wrap;
    }

    private static T FullRect<T>(T node) where T : Control
    {
        node.SetAnchorsPreset(LayoutPreset.FullRect);
        return node;
    }
}
