using Godot;

namespace Ashfall.Client;

/// <summary>
/// Mobile input: drag anywhere to steer, tap to act on a tile. Arrow keys are
/// kept as a desktop fallback so the game is testable in the editor.
/// </summary>
public partial class TouchInput : Node
{
    /// <summary>Drag distance, in pixels, before a gesture counts as movement.</summary>
    private const float DragDeadzone = 12f;

    /// <summary>Drag distance at which steering reaches full speed.</summary>
    private const float DragFullSpeed = 80f;

    /// <summary>Screen position of a tap the player wants to act on.</summary>
    public Vector2? PendingTap { get; private set; }

    private int _touchIndex = -1;
    private Vector2 _touchOrigin;
    private Vector2 _touchCurrent;
    private bool _dragged;

    public Vector2 Direction
    {
        get
        {
            var keyboard = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
            if (keyboard != Vector2.Zero) return keyboard;

            if (_touchIndex < 0) return Vector2.Zero;

            var delta = _touchCurrent - _touchOrigin;
            if (delta.Length() < DragDeadzone) return Vector2.Zero;

            return delta.LimitLength(DragFullSpeed) / DragFullSpeed;
        }
    }

    public Vector2? ConsumeTap()
    {
        var tap = PendingTap;
        PendingTap = null;
        return tap;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventScreenTouch touch when touch.Pressed && _touchIndex < 0:
                _touchIndex = touch.Index;
                _touchOrigin = _touchCurrent = touch.Position;
                _dragged = false;
                break;

            case InputEventScreenTouch touch when !touch.Pressed && touch.Index == _touchIndex:
                // A press that never became a drag is a tap on a tile.
                if (!_dragged) PendingTap = touch.Position;
                _touchIndex = -1;
                break;

            case InputEventScreenDrag drag when drag.Index == _touchIndex:
                _touchCurrent = drag.Position;
                if ((_touchCurrent - _touchOrigin).Length() >= DragDeadzone) _dragged = true;
                break;
        }
    }
}
