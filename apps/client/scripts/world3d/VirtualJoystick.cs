using Godot;

namespace Ashfall.Client;

/// <summary>
/// A left-thumb movement stick for touch. It captures the finger that presses
/// inside it and reports a direction in <see cref="Output"/> (each axis in
/// -1..1, y up), which <see cref="PlayerBody"/> reads as movement input. Because
/// it consumes its own touch as GUI input, the camera drag and world taps —
/// which read *unhandled* input — never see the movement finger, so one thumb
/// walks while the other pans or gathers.
/// </summary>
public partial class VirtualJoystick : Control
{
    /// <summary>Radius the knob can travel from the base centre, in pixels.</summary>
    [Export] public float Radius { get; set; } = 90f;

    /// <summary>Deadzone as a fraction of the radius; below it, output is zero.</summary>
    [Export] public float Deadzone { get; set; } = 0.15f;

    /// <summary>Movement direction, each axis -1..1 with y pointing up (forward).</summary>
    public Vector2 Output { get; private set; } = Vector2.Zero;

    private static readonly Color BaseColour = new(1, 1, 1, 0.12f);
    private static readonly Color RingColour = new(1, 1, 1, 0.35f);
    private static readonly Color KnobColour = new(1, 1, 1, 0.55f);

    private const float KnobRadius = 42f;

    /// <summary>The finger currently steering, or -1 when idle. -2 marks the mouse.</summary>
    private int _activeTouch = -1;
    private Vector2 _knob = Vector2.Zero;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(Radius * 2, Radius * 2);
        MouseFilter = MouseFilterEnum.Stop;
        PivotOffset = Size / 2;
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventScreenTouch touch when touch.Pressed && _activeTouch < 0:
                _activeTouch = touch.Index;
                MoveKnob(touch.Position);
                AcceptEvent();
                break;
            case InputEventScreenTouch touch when !touch.Pressed && touch.Index == _activeTouch:
                Release();
                AcceptEvent();
                break;
            case InputEventScreenDrag drag when drag.Index == _activeTouch:
                MoveKnob(drag.Position);
                AcceptEvent();
                break;

            // Mouse fallback so the stick is testable in the desktop editor.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click when _activeTouch < 0 && click.Pressed:
                _activeTouch = -2;
                MoveKnob(click.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click when _activeTouch == -2 && !click.Pressed:
                Release();
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _activeTouch == -2:
                MoveKnob(motion.Position);
                AcceptEvent();
                break;
        }
    }

    private void MoveKnob(Vector2 local)
    {
        Vector2 fromCentre = local - Size / 2;
        _knob = fromCentre.LimitLength(Radius);

        Vector2 raw = _knob / Radius;
        // Screen y grows downward; movement forward should be positive, so flip y.
        Output = raw.Length() < Deadzone ? Vector2.Zero : raw with { Y = -raw.Y };
        QueueRedraw();
    }

    private void Release()
    {
        _activeTouch = -1;
        _knob = Vector2.Zero;
        Output = Vector2.Zero;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 centre = Size / 2;
        DrawCircle(centre, Radius, BaseColour);
        DrawArc(centre, Radius, 0, Mathf.Tau, 48, RingColour, 2f, true);
        DrawCircle(centre + _knob, KnobRadius, KnobColour);
    }
}
