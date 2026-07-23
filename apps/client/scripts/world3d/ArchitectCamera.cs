using System.Collections.Generic;
using System.Linq;
using Ashfall.SimCore;
using Godot;

namespace Ashfall.Client;

/// <summary>
/// The architect view's camera: a raised, top-down-ish look at a focus point on
/// the ground that the player pans, zooms and turns freely — decoupled from the
/// character, so designing a building means moving the plan around, not walking.
///
/// The reticle is the focus, so the tile it hovers is where a piece lands; aiming
/// it toward a cell's edge is how a wall picks its side. One finger (or a left
/// drag) pans; two fingers pinch to zoom; the mouse wheel zooms in the editor.
/// It becomes the current camera only while build mode is active.
/// </summary>
public partial class ArchitectCamera : Camera3D
{
    /// <summary>Fixed downward tilt, in radians — a comfortable building angle.</summary>
    private const float Pitch = 0.95f;
    private const float MinDistance = 6f;
    private const float MaxDistance = 42f;
    private const float PanPerPixel = 0.0016f;
    private const float ZoomPerWheel = 2.0f;
    private const float PinchZoomScale = 0.05f;

    public bool Active { get; private set; }
    public Vector3 Focus { get; private set; }

    private float _distance = 16f;
    private float _yaw;

    private readonly Dictionary<int, Vector2> _touches = new();
    private float _lastPinch = -1f;

    /// <summary>Points the camera at <paramref name="focus"/> and takes over the view.</summary>
    public void Activate(Vector3 focus)
    {
        Focus = focus;
        Active = true;
        Current = true;
        _touches.Clear();
        _lastPinch = -1f;
        UpdateTransform();
    }

    public void Deactivate()
    {
        Active = false;
        _touches.Clear();
    }

    /// <summary>The tile under the reticle — where the next piece is placed.</summary>
    public (int X, int Y) FocusCell()
    {
        double m = TerrainGenerator.TileMetres;
        return (Mathf.FloorToInt((float)(Focus.X / m)), Mathf.FloorToInt((float)(Focus.Z / m)));
    }

    public override void _Process(double delta)
    {
        if (Active) UpdateTransform();
    }

    private void UpdateTransform()
    {
        // Sit up and back from the focus along the yaw, tilted down by Pitch.
        var offset = new Vector3(
            Mathf.Sin(_yaw) * Mathf.Cos(Pitch),
            Mathf.Sin(Pitch),
            Mathf.Cos(_yaw) * Mathf.Cos(Pitch)) * _distance;
        GlobalPosition = Focus + offset;
        LookAt(Focus, Vector3.Up);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;

        switch (@event)
        {
            case InputEventScreenTouch touch:
                if (touch.Pressed) _touches[touch.Index] = touch.Position;
                else _touches.Remove(touch.Index);
                if (_touches.Count < 2) _lastPinch = -1f;
                break;

            case InputEventScreenDrag drag:
                _touches[drag.Index] = drag.Position;
                if (_touches.Count >= 2) Pinch();
                else Pan(drag.Relative);
                break;

            case InputEventMouseMotion mouse when Input.IsMouseButtonPressed(MouseButton.Left):
                Pan(mouse.Relative);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Zoom(-ZoomPerWheel);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Zoom(ZoomPerWheel);
                break;
        }
    }

    /// <summary>Slides the focus across the ground plane, camera-relative and scaled by
    /// zoom so a drag covers the same screen distance whether near or far.</summary>
    private void Pan(Vector2 screen)
    {
        float scale = _distance * PanPerPixel;
        var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
        var forward = new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw));
        Focus -= right * (screen.X * scale) + forward * (-screen.Y * scale);
    }

    private void Pinch()
    {
        var points = _touches.Values.ToArray();
        float distance = points[0].DistanceTo(points[1]);
        if (_lastPinch > 0f) Zoom((_lastPinch - distance) * PinchZoomScale);
        _lastPinch = distance;
    }

    private void Zoom(float amount) => _distance = Mathf.Clamp(_distance + amount, MinDistance, MaxDistance);
}
