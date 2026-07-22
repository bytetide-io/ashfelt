using Godot;

namespace Ashfall.Client;

/// <summary>
/// Development aid: when ASHFALL_CAPTURE is set to a file path, saves one
/// frame after a short delay and quits. Lets rendering be verified from a
/// terminal, without a human watching the window. Inert otherwise.
///
/// ASHFALL_CAPTURE_AT="x,y" recentres the camera on those tile coordinates and
/// ASHFALL_CAPTURE_ZOOM widens the shot, so a capture can frame a specific
/// feature rather than wherever the player happens to be standing.
/// </summary>
public partial class DebugCapture : Node
{
    [Export] public double DelaySeconds { get; set; } = 6.0;

    private double _remaining;
    private string _path = "";
    private bool _capturing;

    public override void _Ready()
    {
        _path = OS.GetEnvironment("ASHFALL_CAPTURE");
        _remaining = DelaySeconds;
        // Keep running once the tree is paused, so the capture can finish.
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(!string.IsNullOrEmpty(_path));
    }

    private double _walked;
    private bool _walkDone;

    public override void _Process(double delta)
    {
        // ASHFALL_AUTOWALK drives the character from code, so movement and
        // server validation can be verified without a human at the keyboard.
        if (OS.HasEnvironment("ASHFALL_AUTOWALK") && !_walkDone)
        {
            var player = GetNodeOrNull<Node3D>("../Player");
            if (player is not null)
            {
                if (_walked == 0) GD.Print($"[autowalk] start {player.GlobalPosition}");
                _walked += delta;
                Input.ActionPress("move_forward");
                if (_walked > 3.0 && !_walkDone)
                {
                    _walkDone = true;
                    Input.ActionRelease("move_forward");
                    GD.Print($"[autowalk] end {player.GlobalPosition}");
                }
            }
        }

        _remaining -= delta;
        if (_remaining > 0 || _capturing) return;

        _capturing = true;
        Capture();
    }

    private async void Capture()
    {
        // Pausing first stops Main writing the camera position back on the
        // next tick, which would undo the framing below.
        GetTree().Paused = true;

        var camera = GetViewport().GetCamera2D();
        if (camera is not null)
        {
            var zoom = OS.GetEnvironment("ASHFALL_CAPTURE_ZOOM");
            if (!string.IsNullOrEmpty(zoom) && float.TryParse(zoom, out var z))
                camera.Zoom = new Vector2(z, z);

            var at = OS.GetEnvironment("ASHFALL_CAPTURE_AT").Split(',');
            if (at.Length == 2 && float.TryParse(at[0], out var tx) && float.TryParse(at[1], out var ty))
                camera.Position = new Vector2(tx, ty);
        }

        // The viewport texture holds the frame that was already drawn, so new
        // framing only appears once the next one has completed.
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        var viewport = GetViewport();
        viewport.GetTexture().GetImage().SavePng(_path);
        GD.Print($"[capture] wrote {_path} ({viewport.GetVisibleRect().Size})");
        GetTree().Quit();
    }
}
