using Godot;

namespace Ashfall.Client;

/// <summary>
/// Free-look third-person camera. Drag to orbit; the arm collides with terrain
/// so the view never ends up inside a hillside.
///
/// The rig follows the player rather than parenting to it, so the camera does
/// not inherit the character's turning — looking around and walking stay
/// independent, which is what "free look" means.
/// </summary>
public partial class OrbitCamera : Node3D
{
    [Export] public float Sensitivity { get; set; } = 0.005f;
    [Export] public float FollowRate { get; set; } = 12.0f;

    /// <summary>Pitch limits, in degrees, so the camera cannot flip over.</summary>
    [Export] public float MinPitch { get; set; } = -60f;
    [Export] public float MaxPitch { get; set; } = 25f;

    private Node3D _target = null!;
    private SpringArm3D _arm = null!;
    private float _yaw;
    private float _pitch = -20f;

    public override void _Ready()
    {
        _target = GetNode<Node3D>("../Player");
        _arm = GetNode<SpringArm3D>("SpringArm3D");
        _yaw = Rotation.Y;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Touch drag on mobile, mouse drag in the editor.
        Vector2 motion = @event switch
        {
            InputEventScreenDrag drag => drag.Relative,
            InputEventMouseMotion mouse when Input.IsMouseButtonPressed(MouseButton.Left) => mouse.Relative,
            _ => Vector2.Zero,
        };
        if (motion == Vector2.Zero) return;

        _yaw -= motion.X * Sensitivity;
        _pitch = Mathf.Clamp(_pitch - motion.Y * Mathf.RadToDeg(Sensitivity), MinPitch, MaxPitch);
    }

    public override void _Process(double delta)
    {
        // Ease toward the player so physics jitter does not reach the camera.
        GlobalPosition = GlobalPosition.Lerp(
            _target.GlobalPosition + Vector3.Up * 1.4f,
            Mathf.Min(1f, (float)delta * FollowRate));

        Rotation = new Vector3(0, _yaw, 0);
        _arm.Rotation = new Vector3(Mathf.DegToRad(_pitch), 0, 0);
    }
}
