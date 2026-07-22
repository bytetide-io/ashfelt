using Godot;

namespace Ashfall.Client;

/// <summary>
/// Development aid: when ASHFALL_CAPTURE is set to a file path, saves one
/// frame after a short delay and quits. Lets rendering be verified from a
/// terminal, without a human watching the window. Inert otherwise.
/// </summary>
public partial class DebugCapture : Node
{
    [Export] public double DelaySeconds { get; set; } = 6.0;

    private double _remaining;
    private string _path = "";

    public override void _Ready()
    {
        _path = OS.GetEnvironment("ASHFALL_CAPTURE");
        _remaining = DelaySeconds;
        SetProcess(!string.IsNullOrEmpty(_path));
    }

    public override void _Process(double delta)
    {
        _remaining -= delta;
        if (_remaining > 0) return;

        var viewport = GetViewport();
        viewport.GetTexture().GetImage().SavePng(_path);
        GD.Print($"[capture] wrote {_path} ({viewport.GetVisibleRect().Size})");
        GetTree().Quit();
    }
}
